// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Internal;

namespace Microsoft.AspNetCore.WebUtilities
{
    /// <summary>
    /// A Stream that wraps another stream and enables rewinding by buffering the content as it is read.
    /// The content is buffered in memory up to a certain size and then spooled to a temp file on disk.
    /// The temp file will be deleted on Dispose.
    /// </summary>
    public class FileBufferingReadStream : Stream
    {
        private const int _maxRentedBufferSize = 1024 * 1024; // 1MB
        private readonly Stream _inner;
        private readonly ArrayPool<byte> _bytePool;
        private readonly int _memoryThreshold;
        private readonly long? _bufferLimit;
        private string? _tempFileDirectory;
        private readonly Func<string>? _tempFileDirectoryAccessor;
        private string? _tempFileName;

        private Stream _buffer;
        private byte[]? _rentedBuffer;
        private bool _inMemory = true;
        private bool _completelyBuffered;

        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of <see cref="FileBufferingReadStream" />.
        /// </summary>
        /// <param name="inner">The wrapping <see cref="Stream" />.</param>
        /// <param name="memoryThreshold">The maximum size to buffer in memory.</param>
        public FileBufferingReadStream(Stream inner, int memoryThreshold)
            : this(inner, memoryThreshold, bufferLimit: null, tempFileDirectoryAccessor: AspNetCoreTempDirectory.TempDirectoryFactory)
        {
        }

        public FileBufferingReadStream(
            Stream inner,
            int memoryThreshold,
            long? bufferLimit,
            Func<string> tempFileDirectoryAccessor)
            : this(inner, memoryThreshold, bufferLimit, tempFileDirectoryAccessor, ArrayPool<byte>.Shared)
        {
        }

        public FileBufferingReadStream(
            Stream inner,
            int memoryThreshold,
            long? bufferLimit,
            Func<string> tempFileDirectoryAccessor,
            ArrayPool<byte> bytePool)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner));
            }

            if (tempFileDirectoryAccessor == null)
            {
                throw new ArgumentNullException(nameof(tempFileDirectoryAccessor));
            }

            _bytePool = bytePool;
            if (memoryThreshold <= _maxRentedBufferSize)
            {
                _rentedBuffer = bytePool.Rent(memoryThreshold);
                _buffer = new MemoryStream(_rentedBuffer);
                _buffer.SetLength(0);
            }
            else
            {
                _buffer = new MemoryStream();
            }

            _inner = inner;
            _memoryThreshold = memoryThreshold;
            _bufferLimit = bufferLimit;
            _tempFileDirectoryAccessor = tempFileDirectoryAccessor;
        }

        public FileBufferingReadStream(
            Stream inner,
            int memoryThreshold,
            long? bufferLimit,
            string tempFileDirectory)
            : this(inner, memoryThreshold, bufferLimit, tempFileDirectory, ArrayPool<byte>.Shared)
        {
        }

        public FileBufferingReadStream(
            Stream inner,
            int memoryThreshold,
            long? bufferLimit,
            string tempFileDirectory,
            ArrayPool<byte> bytePool)
        {
            if (inner == null)
            {
                throw new ArgumentNullException(nameof(inner));
            }

            if (tempFileDirectory == null)
            {
                throw new ArgumentNullException(nameof(tempFileDirectory));
            }

            _bytePool = bytePool;
            if (memoryThreshold <= _maxRentedBufferSize)
            {
                _rentedBuffer = bytePool.Rent(memoryThreshold);
                _buffer = new MemoryStream(_rentedBuffer);
                _buffer.SetLength(0);
            }
            else
            {
                _buffer = new MemoryStream();
            }

            _inner = inner;
            _memoryThreshold = memoryThreshold;
            _bufferLimit = bufferLimit;
            _tempFileDirectory = tempFileDirectory;
        }

        public bool InMemory
        {
            get { return _inMemory; }
        }

        public string? TempFileName
        {
            get { return _tempFileName; }
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return false; }
        }

        public override long Length
        {
            get { return _buffer.Length; }
        }

        public override long Position
        {
            get { return _buffer.Position; }
            // Note this will not allow seeking forward beyond the end of the buffer.
            set
            {
                ThrowIfDisposed();
                _buffer.Position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            if (!_completelyBuffered && origin == SeekOrigin.End)
            {
                // Can't seek from the end until we've finished consuming the inner stream
                throw new NotSupportedException("The content has not been fully buffered yet.");
            }
            else if (!_completelyBuffered && origin == SeekOrigin.Current && offset + Position > Length)
            {
                // Can't seek past the end of the buffer until we've finished consuming the inner stream
                throw new NotSupportedException("The content has not been fully buffered yet.");
            }
            else if (!_completelyBuffered && origin == SeekOrigin.Begin && offset > Length)
            {
                // Can't seek past the end of the buffer until we've finished consuming the inner stream
                throw new NotSupportedException("The content has not been fully buffered yet.");
            }
            return _buffer.Seek(offset, origin);
        }

        private Stream CreateTempFile()
        {
            if (_tempFileDirectory == null)
            {
                Debug.Assert(_tempFileDirectoryAccessor != null);
                _tempFileDirectory = _tempFileDirectoryAccessor();
                Debug.Assert(_tempFileDirectory != null);
            }

            _tempFileName = Path.Combine(_tempFileDirectory, "ASPNETCORE_" + Guid.NewGuid().ToString() + ".tmp");
            return new FileStream(_tempFileName, FileMode.Create, FileAccess.ReadWrite, FileShare.Delete, 1024 * 16,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            if (_completelyBuffered)
            {
                // Just read from the buffer
                return _buffer.Read(buffer);
            }

            var read = _inner.Read(buffer);

            if (_bufferLimit.HasValue && _bufferLimit - read < _buffer.Length)
            {
                throw new IOException("Buffer limit exceeded.");
            }

            // We're about to go over the threshold, switch to a file
            if (_inMemory && _memoryThreshold - read < _buffer.Length)
            {
                _inMemory = false;
                var oldBuffer = _buffer;
                _buffer = CreateTempFile();
                if (_rentedBuffer == null)
                {
                    // Copy data from the in memory buffer to the file stream using a pooled buffer
                    oldBuffer.Position = 0;
                    var rentedBuffer = _bytePool.Rent(Math.Min((int)oldBuffer.Length, _maxRentedBufferSize));
                    try
                    {
                        var copyRead = oldBuffer.Read(rentedBuffer);
                        while (copyRead > 0)
                        {
                            _buffer.Write(rentedBuffer.AsSpan(0, copyRead));
                            copyRead = oldBuffer.Read(rentedBuffer);
                        }
                    }
                    finally
                    {
                        _bytePool.Return(rentedBuffer);
                    }
                }
                else
                {
                    _buffer.Write(_rentedBuffer.AsSpan(0, (int)oldBuffer.Length));
                    _bytePool.Return(_rentedBuffer);
                    _rentedBuffer = null;
                }
            }

            if (read > 0)
            {
                _buffer.Write(buffer.Slice(0, read));
            }
            else
            {
                _completelyBuffered = true;
            }

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_completelyBuffered)
            {
                // Just read from the buffer
                return await _buffer.ReadAsync(buffer, cancellationToken);
            }

            var read = await _inner.ReadAsync(buffer, cancellationToken);

            if (_bufferLimit.HasValue && _bufferLimit - read < _buffer.Length)
            {
                throw new IOException("Buffer limit exceeded.");
            }

            if (_inMemory && _memoryThreshold - read < _buffer.Length)
            {
                _inMemory = false;
                var oldBuffer = _buffer;
                _buffer = CreateTempFile();
                if (_rentedBuffer == null)
                {
                    oldBuffer.Position = 0;
                    var rentedBuffer = _bytePool.Rent(Math.Min((int)oldBuffer.Length, _maxRentedBufferSize));
                    try
                    {
                        // oldBuffer is a MemoryStream, no need to do async reads.
                        var copyRead = oldBuffer.Read(rentedBuffer);
                        while (copyRead > 0)
                        {
                            await _buffer.WriteAsync(rentedBuffer.AsMemory(0, copyRead), cancellationToken);
                            copyRead = oldBuffer.Read(rentedBuffer);
                        }
                    }
                    finally
                    {
                        _bytePool.Return(rentedBuffer);
                    }
                }
                else
                {
                    await _buffer.WriteAsync(_rentedBuffer.AsMemory(0, (int)oldBuffer.Length), cancellationToken);
                    _bytePool.Return(_rentedBuffer);
                    _rentedBuffer = null;
                }
            }

            if (read > 0)
            {
                await _buffer.WriteAsync(buffer.Slice(0, read), cancellationToken);
            }
            else
            {
                _completelyBuffered = true;
            }

            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            // If we're completed buffered then copy from the underlying source
            if (_completelyBuffered)
            {
                return _buffer.CopyToAsync(destination, bufferSize, cancellationToken);
            }

            async Task CopyToAsyncImpl()
            {
                // At least a 4K buffer
                byte[] buffer = _bytePool.Rent(Math.Min(bufferSize, 4096));
                try
                {
                    while (true)
                    {
                        int bytesRead = await ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }
                        await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    }
                }
                finally
                {
                    _bytePool.Return(buffer);
                }
            }

            return CopyToAsyncImpl();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_rentedBuffer != null)
                {
                    _bytePool.Return(_rentedBuffer);
                }

                if (disposing)
                {
                    _buffer.Dispose();
                }
            }
        }

        public async override ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_rentedBuffer != null)
                {
                    _bytePool.Return(_rentedBuffer);
                }

                await _buffer.DisposeAsync();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FileBufferingReadStream));
            }
        }
    }
}
