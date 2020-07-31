// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components.Profiling;
using Microsoft.AspNetCore.Components.RenderTree;

namespace Microsoft.AspNetCore.Components.Rendering
{
    // IMPORTANT
    //
    // Many of these names are used in code generation. Keep these in sync with the code generation code
    // See: dotnet/aspnetcore-tooling

    /// <summary>
    /// Provides methods for building a collection of <see cref="RenderTreeFrame"/> entries.
    /// </summary>
    public sealed class RenderTreeBuilder : IDisposable
    {
        private readonly static object BoxedTrue = true;
        private readonly static object BoxedFalse = false;
        private readonly static string ComponentReferenceCaptureInvalidParentMessage = $"Component reference captures may only be added as children of frames of type {RenderTreeFrameType.Component}";

        private readonly RenderTreeArrayBuilder _entries = new RenderTreeArrayBuilder();
        private readonly Stack<int> _openElementIndices = new Stack<int>();
        private RenderTreeFrameType? _lastNonAttributeFrameType;
        private bool _hasSeenAddMultipleAttributes;
        private Dictionary<string, int>? _seenAttributeNames;

        /// <summary>
        /// The reserved parameter name used for supplying child content.
        /// </summary>
        private const string ChildContent = nameof(ChildContent);

        /// <summary>
        /// Appends a frame representing an element, i.e., a container for other frames.
        /// In order for the <see cref="RenderTreeBuilder"/> state to be valid, you must
        /// also call <see cref="CloseElement"/> immediately after appending the
        /// new element's child frames.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="elementName">A value representing the type of the element.</param>
        public void OpenElement(int sequence, string elementName)
        {
            ProfilingStart();
            // We are entering a new scope, since we track the "duplicate attributes" per
            // element/component we might need to clean them up now.
            if (_hasSeenAddMultipleAttributes)
            {
                var indexOfLastElementOrComponent = _openElementIndices.Peek();
                ProcessDuplicateAttributes(first: indexOfLastElementOrComponent + 1);
            }

            _openElementIndices.Push(_entries.Count);
            AppendNonAttribute(RenderTreeFrame.Element(sequence, elementName));
            ProfilingEnd();
        }

        /// <summary>
        /// Marks a previously appended element frame as closed. Calls to this method
        /// must be balanced with calls to <see cref="OpenElement(int, string)"/>.
        /// </summary>
        public void CloseElement()
        {
            ProfilingStart();
            var indexOfEntryBeingClosed = _openElementIndices.Pop();

            // We might be closing an element with only attributes, run the duplicate cleanup pass
            // if necessary.
            if (_hasSeenAddMultipleAttributes)
            {
                ProcessDuplicateAttributes(first: indexOfEntryBeingClosed + 1);
            }

            _entries.Buffer[indexOfEntryBeingClosed].ElementSubtreeLength = _entries.Count - indexOfEntryBeingClosed;
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing markup content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="markupContent">Content for the new markup frame.</param>
        public void AddMarkupContent(int sequence, string? markupContent)
        {
            ProfilingStart();
            AppendNonAttribute(RenderTreeFrame.Markup(sequence, markupContent ?? string.Empty));
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing text content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="textContent">Content for the new text frame.</param>
        public void AddContent(int sequence, string? textContent)
        {
            ProfilingStart();
            AppendNonAttribute(RenderTreeFrame.Text(sequence, textContent ?? string.Empty));
            ProfilingEnd();
        }

        /// <summary>
        /// Appends frames representing an arbitrary fragment of content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="fragment">Content to append.</param>
        public void AddContent(int sequence, RenderFragment? fragment)
        {
            ProfilingStart();
            if (fragment != null)
            {
                // We surround the fragment with a region delimiter to indicate that the
                // sequence numbers inside the fragment are unrelated to the sequence numbers
                // outside it. If we didn't do this, the diffing logic might produce inefficient
                // diffs depending on how the sequence numbers compared.
                OpenRegion(sequence);
                fragment(this);
                CloseRegion();
            }
            ProfilingEnd();
        }

        /// <summary>
        /// Appends frames representing an arbitrary fragment of content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="fragment">Content to append.</param>
        /// <param name="value">The value used by <paramref name="fragment"/>.</param>
        public void AddContent<TValue>(int sequence, RenderFragment<TValue>? fragment, TValue value)
        {
            ProfilingStart();
            if (fragment != null)
            {
                AddContent(sequence, fragment(value));
            }
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing markup content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="markupContent">Content for the new markup frame.</param>
        public void AddContent(int sequence, MarkupString markupContent)
            => AddMarkupContent(sequence, markupContent.Value);

        /// <summary>
        /// Appends a frame representing text content.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="textContent">Content for the new text frame.</param>
        public void AddContent(int sequence, object? textContent)
            => AddContent(sequence, textContent?.ToString());

        /// <summary>
        /// <para>
        /// Appends a frame representing a bool-valued attribute with value 'true'.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        public void AddAttribute(int sequence, string name)
        {
            ProfilingStart();

            if (_lastNonAttributeFrameType != RenderTreeFrameType.Element)
            {
                throw new InvalidOperationException($"Valueless attributes may only be added immediately after frames of type {RenderTreeFrameType.Element}");
            }

            _entries.Append(RenderTreeFrame.Attribute(sequence, name, BoxedTrue));
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing a bool-valued attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element. If the value is <c>false</c> and the
        /// current element is not a component, the frame will be omitted.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        public void AddAttribute(int sequence, string name, bool value)
        {
            ProfilingStart();
            AssertCanAddAttribute();
            if (_lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value ? BoxedTrue : BoxedFalse));
            }
            else if (value)
            {
                // Don't add 'false' attributes for elements. We want booleans to map to the presence
                // or absence of an attribute, and false => "False" which isn't falsy in js.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, BoxedTrue));
            }
            else
            {
                TrackAttributeName(name);
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing a string-valued attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element. If the value is <c>null</c> and the
        /// current element is not a component, the frame will be omitted.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        public void AddAttribute(int sequence, string name, string? value)
        {
            ProfilingStart();
            AssertCanAddAttribute();
            if (value != null || _lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value));
            }
            else
            {
                TrackAttributeName(name);
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing a delegate-valued attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element. If the value is <c>null</c> and the
        /// current element is not a component, the frame will be omitted.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        public void AddAttribute(int sequence, string name, MulticastDelegate? value)
        {
            ProfilingStart();
            AssertCanAddAttribute();
            if (value != null || _lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value));
            }
            else
            {
                TrackAttributeName(name);
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing an <see cref="EventCallback"/> attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element. If the value is <c>null</c> and the
        /// current element is not a component, the frame will be omitted.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        /// <remarks>
        /// This method is provided for infrastructure purposes, and is used to support generated code
        /// that uses <see cref="EventCallbackFactory"/>.
        /// </remarks>
        public void AddAttribute(int sequence, string name, EventCallback value)
        {
            ProfilingStart();
            AssertCanAddAttribute();
            if (_lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                // Since this is a component, we need to preserve the type of the EventCallback, so we have
                // to box.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, (object)value));
            }
            else if (value.RequiresExplicitReceiver)
            {
                // If we need to preserve the receiver, we just box the EventCallback
                // so we can get it out on the other side.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, (object)value));
            }
            else if (value.HasDelegate)
            {
                // In the common case the receiver is also the delegate's target, so we
                // just need to retain the delegate. This allows us to avoid an allocation.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value.Delegate));
            }
            else
            {
                // Track the attribute name if needed since we elided the frame.
                TrackAttributeName(name);
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing an <see cref="EventCallback"/> attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element. If the value is <c>null</c> and the
        /// current element is not a component, the frame will be omitted.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        /// <remarks>
        /// This method is provided for infrastructure purposes, and is used to support generated code
        /// that uses <see cref="EventCallbackFactory"/>.
        /// </remarks>
        public void AddAttribute<TArgument>(int sequence, string name, EventCallback<TArgument> value)
        {
            ProfilingStart();
            AssertCanAddAttribute();
            if (_lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                // Since this is a component, we need to preserve the type of the EventCallback, so we have
                // to box.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, (object)value));
            }
            else if (value.RequiresExplicitReceiver)
            {
                // If we need to preserve the receiver - we convert this to an untyped EventCallback. We don't
                // need to preserve the type of an EventCallback<T> when it's invoked from the DOM.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, (object)value.AsUntyped()));
            }
            else if (value.HasDelegate)
            {
                // In the common case the receiver is also the delegate's target, so we
                // just need to retain the delegate. This allows us to avoid an allocation.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value.Delegate));
            }
            else
            {
                // Track the attribute name if needed since we elided the frame.
                TrackAttributeName(name);
            }
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing a string-valued attribute.
        /// The attribute is associated with the most recently added element. If the value is <c>null</c>, or
        /// the <see cref="System.Boolean" /> value <c>false</c> and the current element is not a component, the
        /// frame will be omitted.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="name">The name of the attribute.</param>
        /// <param name="value">The value of the attribute.</param>
        public void AddAttribute(int sequence, string name, object? value)
        {
            ProfilingStart();
            // This looks a bit daunting because we need to handle the boxed/object version of all of the
            // types that AddAttribute special cases.
            if (_lastNonAttributeFrameType == RenderTreeFrameType.Element)
            {
                if (value == null)
                {
                    // Treat 'null' attribute values for elements as a conditional attribute.
                    TrackAttributeName(name);
                }
                else if (value is bool boolValue)
                {
                    if (boolValue)
                    {
                        _entries.Append(RenderTreeFrame.Attribute(sequence, name, BoxedTrue));
                    }
                    else
                    {
                        // Don't add anything for false bool value.
                        TrackAttributeName(name);
                    }
                }
                else if (value is IEventCallback callbackValue)
                {
                    if (callbackValue.HasDelegate)
                    {
                        _entries.Append(RenderTreeFrame.Attribute(sequence, name, callbackValue.UnpackForRenderTree()));
                    }
                    else
                    {
                        TrackAttributeName(name);
                    }
                }
                else if (value is MulticastDelegate)
                {
                    _entries.Append(RenderTreeFrame.Attribute(sequence, name, value));
                }
                else
                {
                    // The value is either a string, or should be treated as a string.
                    _entries.Append(RenderTreeFrame.Attribute(sequence, name, value.ToString()));
                }
            }
            else if (_lastNonAttributeFrameType == RenderTreeFrameType.Component)
            {
                // If this is a component, we always want to preserve the original type.
                _entries.Append(RenderTreeFrame.Attribute(sequence, name, value));
            }
            else
            {
                // This is going to throw. Calling it just to get a consistent exception message.
                AssertCanAddAttribute();
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Appends a frame representing an attribute.
        /// </para>
        /// <para>
        /// The attribute is associated with the most recently added element.
        /// </para>
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="frame">A <see cref="RenderTreeFrame"/> holding the name and value of the attribute.</param>
        public void AddAttribute(int sequence, RenderTreeFrame frame)
        {
            ProfilingStart();
            if (frame.FrameType != RenderTreeFrameType.Attribute)
            {
                throw new ArgumentException($"The {nameof(frame.FrameType)} must be {RenderTreeFrameType.Attribute}.");
            }

            AssertCanAddAttribute();
            frame.Sequence = sequence;
            _entries.Append(frame);
            ProfilingEnd();
        }

        /// <summary>
        /// Adds frames representing multiple attributes with the same sequence number.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="attributes">A collection of key-value pairs representing attributes.</param>
        public void AddMultipleAttributes(int sequence, IEnumerable<KeyValuePair<string, object>>? attributes)
        {
            ProfilingStart();
            // Calling this up-front just to make sure we validate before mutating anything.
            AssertCanAddAttribute();

            if (attributes != null)
            {
                _hasSeenAddMultipleAttributes = true;

                foreach (var attribute in attributes)
                {
                    // This will call the AddAttribute(int, string, object) overload.
                    //
                    // This is fine because we try to make the object overload behave identically
                    // to the others.
                    AddAttribute(sequence, attribute.Key, attribute.Value);
                }
            }
            ProfilingEnd();
        }

        /// <summary>
        /// <para>
        /// Indicates that the preceding attribute represents an event handler
        /// whose execution updates the attribute with name <paramref name="updatesAttributeName"/>.
        /// </para>
        /// <para>
        /// This information is used by the rendering system to determine whether
        /// to accept a value update for the other attribute when receiving a
        /// call to the event handler.
        /// </para>
        /// </summary>
        /// <param name="updatesAttributeName">The name of another attribute whose value can be updated when the event handler is executed.</param>
        public void SetUpdatesAttributeName(string updatesAttributeName)
        {
            ProfilingStart();
            if (_entries.Count == 0)
            {
                throw new InvalidOperationException("No preceding attribute frame exists.");
            }

            ref var prevFrame = ref _entries.Buffer[_entries.Count - 1];
            if (prevFrame.FrameType != RenderTreeFrameType.Attribute)
            {
                throw new InvalidOperationException($"Incorrect frame type: '{prevFrame.FrameType}'");
            }

            prevFrame.AttributeEventUpdatesAttributeName = updatesAttributeName;
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing a child component.
        /// </summary>
        /// <typeparam name="TComponent">The type of the child component.</typeparam>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        public void OpenComponent<TComponent>(int sequence) where TComponent : notnull, IComponent
            => OpenComponentUnchecked(sequence, typeof(TComponent));

        /// <summary>
        /// Appends a frame representing a child component.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="componentType">The type of the child component.</param>
        public void OpenComponent(int sequence, Type componentType)
        {
            if (!typeof(IComponent).IsAssignableFrom(componentType))
            {
                throw new ArgumentException($"The component type must implement {typeof(IComponent).FullName}.");
            }

            OpenComponentUnchecked(sequence, componentType);
        }

        /// <summary>
        /// Assigns the specified key value to the current element or component.
        /// </summary>
        /// <param name="value">The value for the key.</param>
        public void SetKey(object? value)
        {
            ProfilingStart();
            if (value == null)
            {
                // Null is equivalent to not having set a key, which is valuable because Razor syntax doesn't have an
                // easy way to have conditional directive attributes
                ProfilingEnd();
                return;
            }

            var parentFrameIndex = GetCurrentParentFrameIndex();
            if (!parentFrameIndex.HasValue)
            {
                throw new InvalidOperationException("Cannot set a key outside the scope of a component or element.");
            }

            var parentFrameIndexValue = parentFrameIndex.Value;
            ref var parentFrame = ref _entries.Buffer[parentFrameIndexValue];
            switch (parentFrame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    parentFrame.ElementKey = value; // It's a ref var, so this writes to the array
                    break;
                case RenderTreeFrameType.Component:
                    parentFrame.ComponentKey = value; // It's a ref var, so this writes to the array
                    break;
                default:
                    throw new InvalidOperationException($"Cannot set a key on a frame of type {parentFrame.FrameType}.");
            }
            ProfilingEnd();
        }

        private void OpenComponentUnchecked(int sequence, Type componentType)
        {
            ProfilingStart();
            // We are entering a new scope, since we track the "duplicate attributes" per
            // element/component we might need to clean them up now.
            if (_hasSeenAddMultipleAttributes)
            {
                var indexOfLastElementOrComponent = _openElementIndices.Peek();
                ProcessDuplicateAttributes(first: indexOfLastElementOrComponent + 1);
            }

            _openElementIndices.Push(_entries.Count);
            AppendNonAttribute(RenderTreeFrame.ChildComponent(sequence, componentType));
            ProfilingEnd();
        }

        /// <summary>
        /// Marks a previously appended component frame as closed. Calls to this method
        /// must be balanced with calls to <see cref="OpenComponent{TComponent}"/>.
        /// </summary>
        public void CloseComponent()
        {
            ProfilingStart();
            var indexOfEntryBeingClosed = _openElementIndices.Pop();

            // We might be closing a component with only attributes. Run the attribute cleanup pass
            // if necessary.
            if (_hasSeenAddMultipleAttributes)
            {
                ProcessDuplicateAttributes(first: indexOfEntryBeingClosed + 1);
            }

            _entries.Buffer[indexOfEntryBeingClosed].ComponentSubtreeLength = _entries.Count - indexOfEntryBeingClosed;
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing an instruction to capture a reference to the parent element.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="elementReferenceCaptureAction">An action to be invoked whenever the reference value changes.</param>
        public void AddElementReferenceCapture(int sequence, Action<ElementReference> elementReferenceCaptureAction)
        {
            ProfilingStart();
            if (GetCurrentParentFrameType() != RenderTreeFrameType.Element)
            {
                throw new InvalidOperationException($"Element reference captures may only be added as children of frames of type {RenderTreeFrameType.Element}");
            }

            AppendNonAttribute(RenderTreeFrame.ElementReferenceCapture(sequence, elementReferenceCaptureAction));
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing an instruction to capture a reference to the parent component.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        /// <param name="componentReferenceCaptureAction">An action to be invoked whenever the reference value changes.</param>
        public void AddComponentReferenceCapture(int sequence, Action<object?> componentReferenceCaptureAction)
        {
            ProfilingStart();
            var parentFrameIndex = GetCurrentParentFrameIndex();
            if (!parentFrameIndex.HasValue)
            {
                throw new InvalidOperationException(ComponentReferenceCaptureInvalidParentMessage);
            }

            var parentFrameIndexValue = parentFrameIndex.Value;
            if (_entries.Buffer[parentFrameIndexValue].FrameType != RenderTreeFrameType.Component)
            {
                throw new InvalidOperationException(ComponentReferenceCaptureInvalidParentMessage);
            }

            AppendNonAttribute(RenderTreeFrame.ComponentReferenceCapture(sequence, componentReferenceCaptureAction, parentFrameIndexValue));
            ProfilingEnd();
        }

        /// <summary>
        /// Appends a frame representing a region of frames.
        /// </summary>
        /// <param name="sequence">An integer that represents the position of the instruction in the source code.</param>
        public void OpenRegion(int sequence)
        {
            ProfilingStart();
            // We are entering a new scope, since we track the "duplicate attributes" per
            // element/component we might need to clean them up now.
            if (_hasSeenAddMultipleAttributes)
            {
                var indexOfLastElementOrComponent = _openElementIndices.Peek();
                ProcessDuplicateAttributes(first: indexOfLastElementOrComponent + 1);
            }

            _openElementIndices.Push(_entries.Count);
            AppendNonAttribute(RenderTreeFrame.Region(sequence));
            ProfilingEnd();
        }

        /// <summary>
        /// Marks a previously appended region frame as closed. Calls to this method
        /// must be balanced with calls to <see cref="OpenRegion(int)"/>.
        /// </summary>
        public void CloseRegion()
        {
            ProfilingStart();
            var indexOfEntryBeingClosed = _openElementIndices.Pop();
            _entries.Buffer[indexOfEntryBeingClosed].RegionSubtreeLength = _entries.Count - indexOfEntryBeingClosed;
            ProfilingEnd();
        }

        private void AssertCanAddAttribute()
        {
            if (_lastNonAttributeFrameType != RenderTreeFrameType.Element
                && _lastNonAttributeFrameType != RenderTreeFrameType.Component)
            {
                throw new InvalidOperationException($"Attributes may only be added immediately after frames of type {RenderTreeFrameType.Element} or {RenderTreeFrameType.Component}");
            }
        }

        private int? GetCurrentParentFrameIndex()
            => _openElementIndices.Count == 0 ? (int?)null : _openElementIndices.Peek();

        private RenderTreeFrameType? GetCurrentParentFrameType()
        {
            var parentIndex = GetCurrentParentFrameIndex();
            return parentIndex.HasValue
                ? _entries.Buffer[parentIndex.Value].FrameType
                : (RenderTreeFrameType?)null;
        }

        /// <summary>
        /// Clears the builder.
        /// </summary>
        public void Clear()
        {
            ProfilingStart();
            _entries.Clear();
            _openElementIndices.Clear();
            _lastNonAttributeFrameType = null;
            _hasSeenAddMultipleAttributes = false;
            _seenAttributeNames?.Clear();
            ProfilingEnd();
        }

        // internal because this should only be used during the post-event tree patching logic
        // It's expensive because it involves copying all the subsequent memory in the array
        internal void InsertAttributeExpensive(int insertAtIndex, int sequence, string attributeName, object? attributeValue)
        {
            ProfilingStart();
            // Replicate the same attribute omission logic as used elsewhere
            if ((attributeValue == null) || (attributeValue is bool boolValue && !boolValue))
            {
                ProfilingEnd();
                return;
            }

            _entries.InsertExpensive(insertAtIndex, RenderTreeFrame.Attribute(sequence, attributeName, attributeValue));
            ProfilingEnd();
        }

        /// <summary>
        /// Returns the <see cref="RenderTreeFrame"/> values that have been appended.
        /// </summary>
        /// <returns>An array range of <see cref="RenderTreeFrame"/> values.</returns>
        public ArrayRange<RenderTreeFrame> GetFrames() =>
            _entries.ToRange();

        private void AppendNonAttribute(in RenderTreeFrame frame)
        {
            _entries.Append(frame);
            _lastNonAttributeFrameType = frame.FrameType;
        }

        // Internal for testing
        internal void ProcessDuplicateAttributes(int first)
        {
            ProfilingStart();
            Debug.Assert(_hasSeenAddMultipleAttributes);

            // When AddMultipleAttributes method has been called, we need to postprocess attributes while closing
            // the element/component. However, we also don't know the end index we should look at because it
            // will contain nested content.
            var buffer = _entries.Buffer;
            var last = _entries.Count - 1;

            for (var i = first; i <= last; i++)
            {
                if (buffer[i].FrameType != RenderTreeFrameType.Attribute)
                {
                    last = i - 1;
                    break;
                }
            }

            // Now that we've found the last attribute, we can iterate backwards and process duplicates.
            var seenAttributeNames = (_seenAttributeNames ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            for (var i = last; i >= first; i--)
            {
                ref var frame = ref buffer[i];
                Debug.Assert(frame.FrameType == RenderTreeFrameType.Attribute, $"Frame type is {frame.FrameType} at {i}");

                if (!seenAttributeNames.TryGetValue(frame.AttributeName, out var index))
                {
                    // This is the first time seeing this attribute name. Add to the dictionary and move on.
                    seenAttributeNames.Add(frame.AttributeName, i);
                }
                else if (index < i)
                {
                    // This attribute is overriding a "silent frame" where we didn't create a frame for an AddAttribute call.
                    // This is the case for a null event handler, or bool false value.
                    //
                    // We need to update our tracking, in case the attribute appeared 3 or more times.
                    seenAttributeNames[frame.AttributeName] = i;
                }
                else if (index > i)
                {
                    // This attribute has been overridden. For now, blank out its name to *mark* it. We'll do a pass
                    // later to wipe it out.
                    frame = default;
                }
                else
                {
                    // OK so index == i. How is that possible? Well it's possible for a "silent frame" immediately
                    // followed by setting the same attribute. Think of it this way, when we create a "silent frame"
                    // we have to track that attribute name with *some* index.
                    //
                    // The only index value we can safely use is _entries.Count (next available). This is fine because
                    // we never use these indexes to look stuff up, only for comparison.
                    //
                    // That gets you here, and there's no action to take.
                }
            }

            // This is the pass where we cleanup attributes that have been wiped out.
            //
            // We copy the entries we're keeping into the earlier parts of the list (preserving order).
            //
            // Note that we iterate to the end of the list here, there might be additional frames after the attributes
            // (ref) or content) that need to move to the left.
            var offset = first;
            for (var i = first; i < _entries.Count; i++)
            {
                ref var frame = ref buffer[i];
                if (frame.FrameType != RenderTreeFrameType.None)
                {
                    buffer[offset++] = frame;
                }
            }

            // Clean up now unused space at the end of the list.
            var residue = _entries.Count - offset;
            for (var i = 0; i < residue; i++)
            {
                _entries.RemoveLast();
            }

            seenAttributeNames.Clear();
            _hasSeenAddMultipleAttributes = false;
            ProfilingEnd();
        }

        // Internal for testing
        internal void TrackAttributeName(string name)
        {
            if (!_hasSeenAddMultipleAttributes)
            {
                return;
            }

            var seenAttributeNames = (_seenAttributeNames ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            seenAttributeNames[name] = _entries.Count; // See comment in ProcessAttributes for why this is OK.
        }

        void IDisposable.Dispose()
        {
            ProfilingStart();
            _entries.Dispose();
            ProfilingEnd();
        }

        // Having too many calls to ComponentsProfiling.Instance.Start/End has a measurable perf impact
        // even when capturing is disabled. So, to enable detailed profiling for this class, define the
        // Profile_RenderTreeBuilder compiler symbol, otherwise the calls are compiled out entirely.
        // Enabling detailed profiling adds about 5% to rendering benchmark times.

        [Conditional("Profile_RenderTreeBuilder")]
        private static void ProfilingStart([CallerMemberName] string? name = null)
            => ComponentsProfiling.Instance.Start(name);

        [Conditional("Profile_RenderTreeBuilder")]
        private static void ProfilingEnd([CallerMemberName] string? name = null)
            => ComponentsProfiling.Instance.End(name);
    }
}
