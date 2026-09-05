namespace OpenTui.Blazor.Rendering;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging;
using OpenTui.Blazor.Nodes;
using OpenTui.Native;
using OpenTui.Blazor.Components;
using System.Runtime.ExceptionServices;

public sealed partial class TuiRenderer(IServiceProvider services, ILoggerFactory loggerFactory, TimeProvider? clock = null) : Renderer(services, loggerFactory)
{
    public TimeProvider Clock { get; } = clock ?? services.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;
    private readonly Dictionary<int, TuiNode> _components = [];
    private readonly Dictionary<IComponent, int> _roots = [];
    public TuiNode RootNode { get; } = new() { TagName = "root", Grow = 1,
        Fg = TerminalRenderColors.Default.Foreground, Bg = TerminalRenderColors.Default.Background };
    private TerminalRenderColors _colors = TerminalRenderColors.Default;
    private TerminalImageContext _imageContext = new(null, 0, 0);
    public TerminalImageContext ImageContext
    {
        get => _imageContext;
        set { if (_imageContext == value) return; _imageContext = value; Dirty = true; }
    }
    public TerminalRenderColors Colors
    {
        get => _colors;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _colors = value;
            RootNode.Fg = value.Foreground;
            RootNode.Bg = value.Background;
            Dirty = true;
        }
    }
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
    public bool Dirty { get; set; } = true;
    public Exception? Error { get; private set; }
    private TuiNode? _focused;
    private readonly List<Task> _callbacks = [];
    private readonly CancellationTokenSource _eventCancellation = new();
    private bool _eventsStopped;
    private CancellationToken _stoppingToken;
    private readonly List<(TuiNode Modal, TuiNode? Previous)> _modalFocus = [];

    private TuiNode FocusScope => _modalFocus.Count > 0 ? _modalFocus[^1].Modal : RootNode;

    public bool Focus(string key)
    {
        Dispatcher.AssertAccess();
        var node = Inputs(FocusScope).FirstOrDefault(node => node.FocusKey == key && Visible(node));
        if (node is null) return false;
        SetFocus(node);
        return true;
    }

    public void EnsureFocus()
    {
        Dispatcher.AssertAccess();
        EnsureSelection();
        var modals = Modals(RootNode).Where(Visible).OrderBy(modal => modal.ZIndex).ToList();
        while (_modalFocus.Count > 0 && !modals.Contains(_modalFocus[^1].Modal))
        {
            var previous = _modalFocus[^1].Previous;
            _modalFocus.RemoveAt(_modalFocus.Count - 1);
            SetFocus(previous);
        }
        foreach (var modal in modals)
        {
            if (_modalFocus.Any(entry => ReferenceEquals(entry.Modal, modal))) continue;
            _modalFocus.Add((modal, _focused));
            SetFocus(null);
        }
        if (Inputs(FocusScope).FirstOrDefault(node => node.EmbeddedTerminal?.FocusRequested == true && Visible(node)) is { } requested)
        {
            requested.EmbeddedTerminal!.FocusRequested = false;
            SetFocus(requested);
            return;
        }
        if (_focused is not null && Visible(_focused))
            for (var parent = _focused; parent is not null; parent = parent.Parent)
                if (ReferenceEquals(parent, FocusScope)) return;
        SetFocus(Inputs(FocusScope).FirstOrDefault(Visible));
    }

    private static IEnumerable<TuiNode> Modals(TuiNode node)
    {
        if (node.TagName == "modal") yield return node;
        foreach (var child in node.LayoutChildren)
            foreach (var modal in Modals(child)) yield return modal;
    }

    private static bool Visible(TuiNode node)
    {
        if (node.TagName == "modal") return node.LayoutWidth > 0 && node.LayoutHeight > 0;
        var left = node.X;
        var top = node.Y;
        var right = (long)node.X + node.LayoutWidth;
        var bottom = (long)node.Y + node.LayoutHeight;
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.TagName == "#component") continue;
            left = Math.Max(left, parent.X);
            top = Math.Max(top, parent.Y);
            right = Math.Min(right, (long)parent.X + parent.LayoutWidth);
            bottom = Math.Min(bottom, (long)parent.Y + parent.LayoutHeight);
            if (parent.TagName == "modal") break;
        }
        return right > left && bottom > top;
    }

    private void SetFocus(TuiNode? node)
    {
        if (ReferenceEquals(_focused, node)) return;
        if (_focused is not null) { _focused.Focused = false; _focused.EmbeddedTerminal?.Focus(false); }
        _focused = node;
        if (node is not null) { node.Focused = true; node.EmbeddedTerminal?.Focus(true); }
        Dirty = true;
    }

    private static IEnumerable<TuiNode> Inputs(TuiNode node)
    {
        if (node.TagName == "input" || node.EmbeddedTerminal is not null || node.FocusKey is not null && node.KeyHandlerId != 0) yield return node;
        foreach (var child in node.LayoutChildren)
            foreach (var input in Inputs(child)) yield return input;
    }

    public bool DispatchKey(ConsoleKeyInfo key, TuiLayoutEngine layout)
    {
        Dispatcher.AssertAccess();
        if (_eventsStopped) return false;
        EnsureFocus();
        if (_modalFocus.Count > 0 && (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)
            && (_focused?.TagName != "input" || string.IsNullOrEmpty(_focused.Content))))
        {
            var modal = _modalFocus[^1].Modal;
            if (modal.CloseHandlerId != 0) TrackCallback(DispatchEventAsync(modal.CloseHandlerId, null, EventArgs.Empty));
            return true;
        }
        if (key.Key == ConsoleKey.Tab && (key.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) == ConsoleModifiers.None)
        {
            var inputs = Inputs(FocusScope).Where(Visible).ToList();
            if (inputs.Count > 1)
            {
                var next = (inputs.IndexOf(_focused!) + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1) + inputs.Count) % inputs.Count;
                SetFocus(inputs[next]);
                return true;
            }
        }
        if (_focused is not { KeyHandlerId: > 0 } node) return _modalFocus.Count > 0;
        var args = new TerminalKeyEventArgs(key, (text, width) => layout.MeasureInput(text, width ?? Math.Max(1, node.LayoutWidth)), _eventCancellation.Token);
        TrackCallback(DispatchEventAsync(node.KeyHandlerId, null, args));
        return args.Handled || _modalFocus.Count > 0;
    }

    public Keymap.KeymapContext KeymapContext(TuiLayoutEngine layout)
    {
        Dispatcher.AssertAccess();
        EnsureFocus();
        var path = new List<object>();
        for (var node = _focused ?? RootNode; node is not null; node = node.Parent) path.Add(node);
        return new()
        {
            FocusedTarget = _focused,
            FocusPath = path,
            Data = new Dictionary<string, object?>
            {
                ["terminal.modal"] = _modalFocus.Count > 0,
                ["terminal.editor"] = _focused?.TagName == "input",
                ["terminal.multiline"] = _focused?.TagName == "input" && _focused.MaxHeight > 1,
                ["terminal.focusKey"] = _focused?.FocusKey,
                ["terminal.measure"] = (Func<string, int?, TerminalTextLayout>)((text, width) => layout.MeasureInput(text, width ?? Math.Max(1, _focused?.LayoutWidth ?? 1)))
            }
        };
    }

    public bool DispatchPaste(string text)
    {
        Dispatcher.AssertAccess();
        if (_eventsStopped) return false;
        EnsureFocus();
        if (_focused?.EmbeddedTerminal is { } terminal) { terminal.Paste(text); return true; }
        if (_focused is not { PasteHandlerId: > 0 } node) return _modalFocus.Count > 0;
        var args = new TerminalPasteEventArgs(text, _eventCancellation.Token);
        TrackCallback(DispatchEventAsync(node.PasteHandlerId, null, args));
        return args.Handled || _modalFocus.Count > 0;
    }

    private void TrackCallback(Task callback)
    {
        if (callback.IsCompleted) callback.GetAwaiter().GetResult();
        else _callbacks.Add(callback);
    }

    public void ObservePendingEvents()
    {
        Dispatcher.AssertAccess();
        for (var i = _callbacks.Count - 1; i >= 0; i--)
        {
            if (!_callbacks[i].IsCompleted) continue;
            var callback = _callbacks[i];
            _callbacks.RemoveAt(i);
            callback.GetAwaiter().GetResult();
        }
    }

    public async Task StopPendingEventsAsync()
    {
        Dispatcher.AssertAccess();
        if (_eventsStopped) return;
        _eventsStopped = true;
        _stoppingToken = _eventCancellation.Token;
        var failures = new List<Exception>();
        try
        {
            await Observe(_eventCancellation.CancelAsync());
            foreach (var callback in _callbacks.ToArray()) await Observe(callback);
        }
        finally
        {
            _callbacks.Clear();
            _eventCancellation.Dispose();
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Terminal callback shutdown failed.", failures);

        async Task Observe(Task callback)
        {
            try { await callback; }
            catch (Exception exception)
            {
                IEnumerable<Exception> errors = exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception];
                foreach (var error in errors)
                    if (!IsShutdownCancellation(error)) failures.Add(error);
            }
        }
    }

    public Task<TComponent> AttachRootComponentAsync<TComponent>(ParameterView? parameters = null) where TComponent : IComponent =>
        Dispatcher.InvokeAsync(async () =>
        {
            var component = (TComponent)InstantiateComponent(typeof(TComponent));
            var id = AssignRootComponentId(component);
            var node = new TuiNode { TagName = "#component", Component = component };
            _components.Add(id, node);
            _roots.Add(component, id);
            RootNode.AddChild(node);
            try
            {
                await RenderRootComponentAsync(id, parameters ?? ParameterView.Empty);
            }
            catch
            {
                await DetachRootComponentAsync(component);
                throw;
            }
            return component;
        });

    public Task DetachRootComponentAsync(IComponent component) => Dispatcher.InvokeAsync(() =>
    {
        if (!_roots.Remove(component, out var id)) return;
        RemoveRootComponent(id);
        if (_components.Remove(id, out var node))
        {
            node.ReleaseNativeText();
            node.Parent?.RemoveChild(node);
        }
        Dirty = true;
    });

    protected override Task UpdateDisplayAsync(in RenderBatch batch)
    {
        for (var i = 0; i < batch.UpdatedComponents.Count; i++)
        {
            var diff = batch.UpdatedComponents.Array[i];
            if (!_components.TryGetValue(diff.ComponentId, out var component))
                throw new InvalidOperationException($"Missing terminal component {diff.ComponentId}.");
            ApplyEdits(component, diff, batch.ReferenceFrames.Array);
            // Custom data cannot travel through ordinary element attributes: Blazor
            // stringifies those. The generic text component owns its immutable runs.
            if (component.Component is TuiText text && component.Children.Count == 1)
                component.Children[0].TextRuns = text.Runs;
            if (component.Component is Input input && component.Children.Count == 1)
                component.Children[0].TextRuns = input.MarkRuns;
            if (component.Component is TuiCode code && component.Children.Count == 1)
                component.Children[0].CodeDocument = code.Document;
            if (component.Component is ScrollBox scroll && component.Children.Count == 1)
                component.Children[0].ScrollState = scroll.State;
            if (component.Component is EmbeddedTerminal terminal && component.Children.Count == 1)
                component.Children[0].EmbeddedTerminal = terminal.State;
            if (component.Component is TuiImage image && component.Children.Count == 1)
            {
                component.Children[0].Image = image.State;
                component.Children[0].ImageFit = image.Fit;
                component.Children[0].ImageProtocol = image.Protocol;
            }
        }
        for (var i = 0; i < batch.DisposedComponentIDs.Count; i++)
        {
            if (_components.Remove(batch.DisposedComponentIDs.Array[i], out var component))
            {
                component.ReleaseNativeText();
                component.Parent?.RemoveChild(component);
            }
        }
        Dirty = true;
        return Task.CompletedTask;
    }

    private void ApplyEdits(TuiNode component, in RenderTreeDiff diff, RenderTreeFrame[] frames)
    {
        var current = component;
        var parents = new Stack<TuiNode>();
        List<(int From, int To)>? permutation = null;
        // Edits is a segment of Blazor's shared edit array, not a zero-based range.
        for (var i = 0; i < diff.Edits.Count; i++)
        {
            var edit = diff.Edits.Array[diff.Edits.Offset + i];
            switch (edit.Type)
            {
                case RenderTreeEditType.PrependFrame:
                    InsertFrame(current, edit.SiblingIndex, frames, edit.ReferenceFrameIndex);
                    break;
                case RenderTreeEditType.RemoveFrame:
                    current.Children[edit.SiblingIndex].ReleaseNativeText();
                    current.RemoveChild(current.Children[edit.SiblingIndex]);
                    break;
                case RenderTreeEditType.SetAttribute:
                    var attribute = frames[edit.ReferenceFrameIndex];
                    ApplyAttribute(current.Children[edit.SiblingIndex], attribute.AttributeName, attribute.AttributeValue, attribute.AttributeEventHandlerId);
                    break;
                case RenderTreeEditType.RemoveAttribute:
                    ApplyAttribute(current.Children[edit.SiblingIndex], edit.RemovedAttributeName, null);
                    break;
                case RenderTreeEditType.UpdateText:
                    current.Children[edit.SiblingIndex].TextContent = frames[edit.ReferenceFrameIndex].TextContent;
                    break;
                case RenderTreeEditType.UpdateMarkup:
                    current.Children[edit.SiblingIndex].TextContent = frames[edit.ReferenceFrameIndex].MarkupContent;
                    break;
                case RenderTreeEditType.StepIn:
                    parents.Push(current);
                    current = current.Children[edit.SiblingIndex];
                    break;
                case RenderTreeEditType.StepOut:
                    current = parents.Pop();
                    break;
                case RenderTreeEditType.PermutationListEntry:
                    (permutation ??= []).Add((edit.SiblingIndex, edit.MoveToSiblingIndex));
                    break;
                case RenderTreeEditType.PermutationListEnd:
                    current.ApplyPermutation(permutation ?? throw new InvalidOperationException("A render permutation ended without entries."));
                    permutation = null;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported terminal render edit: {edit.Type}.");
            }
        }
    }

    private int InsertFrame(TuiNode parent, int sibling, RenderTreeFrame[] frames, int index)
    {
        var frame = frames[index];
        switch (frame.FrameType)
        {
            case RenderTreeFrameType.Element:
                var node = new TuiNode { TagName = frame.ElementName, Key = frame.ElementKey };
                var end = index + frame.ElementSubtreeLength;
                var next = index + 1;
                while (next < end && frames[next].FrameType == RenderTreeFrameType.Attribute)
                {
                    ApplyAttribute(node, frames[next].AttributeName, frames[next].AttributeValue, frames[next].AttributeEventHandlerId);
                    next++;
                }
                while (next < end)
                {
                    InsertFrame(node, node.Children.Count, frames, next);
                    next += SubtreeLength(frames[next]);
                }
                parent.InsertChild(sibling, node);
                return 1;
            case RenderTreeFrameType.Component:
                // Component boundaries count as one sibling in diffs, but are
                // transparent to layout. Their children retain their own identity.
                if (!_components.TryGetValue(frame.ComponentId, out var component))
                    _components.Add(frame.ComponentId, component = new TuiNode { TagName = "#component", Component = frame.Component, Key = frame.ComponentKey });
                parent.InsertChild(sibling, component);
                return 1;
            case RenderTreeFrameType.Region:
                var inserted = 0;
                for (var child = index + 1; child < index + frame.RegionSubtreeLength; child += SubtreeLength(frames[child]))
                    inserted += InsertFrame(parent, sibling + inserted, frames, child);
                return inserted;
            case RenderTreeFrameType.Text:
            case RenderTreeFrameType.Markup:
                parent.InsertChild(sibling, new TuiNode
                {
                    TagName = "#text",
                    IsMarkup = frame.FrameType == RenderTreeFrameType.Markup,
                    TextContent = frame.FrameType == RenderTreeFrameType.Text ? frame.TextContent : frame.MarkupContent
                });
                return 1;
            default:
                return 0;
        }
    }

    private static int SubtreeLength(RenderTreeFrame frame) => frame.FrameType switch
    {
        RenderTreeFrameType.Element => frame.ElementSubtreeLength,
        RenderTreeFrameType.Component => frame.ComponentSubtreeLength,
        RenderTreeFrameType.Region => frame.RegionSubtreeLength,
        _ => 1
    };

    private static NativeRgba? Color(TuiNode node, string name, object? value)
    {
        if (value is null) return null;
        if (value is not string hex || hex.Length is not (7 or 9) || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            throw InvalidAttribute(node, name, value, "a #RRGGBB or #RRGGBBAA color");
        return hex.Length == 9 ? new NativeRgba((byte)(rgb >> 24), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : new NativeRgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static void ApplyAttribute(TuiNode node, string? name, object? value, ulong handlerId = 0)
    {
        switch (name)
        {
            case "direction":
                node.Direction = value is null ? TuiFlexDirection.Column
                    : value is TuiFlexDirection direction && Enum.IsDefined(direction) ? direction
                    : string.Equals(value as string, "row", StringComparison.OrdinalIgnoreCase) ? TuiFlexDirection.Row
                    : string.Equals(value as string, "column", StringComparison.OrdinalIgnoreCase) ? TuiFlexDirection.Column
                    : throw InvalidAttribute(node, name, value, "row or column");
                break;
            case "border":
                node.BorderStyle = value is null or "none" ? null : value is "single" or "double" or "left" or "rounded" or "heavy"
                    ? (string)value : throw InvalidAttribute(node, name, value, "single, double, left, rounded, heavy, or none");
                break;
            case "border-fg": node.BorderFg = Color(node, name, value); break;
            case "width": node.Width = Number(node, name, value); break;
            case "height": node.Height = Number(node, name, value); break;
            case "grow": node.Grow = Number(node, name, value) ?? 0; break;
            case "shrink": node.Shrink = Number(node, name, value) ?? 0; break;
            case "gap": node.Gap = Number(node, name, value) ?? 0; break;
            case "center": node.Center = Boolean(node, name, value); break;
            case "position":
                node.Position = value is null ? TuiPosition.Flow
                    : Enum.TryParse<TuiPosition>(value.ToString(), true, out var position) && Enum.IsDefined(position)
                        ? position : throw InvalidAttribute(node, name, value, "flow or absolute");
                node.Parent?.InvalidateLayoutChildren();
                break;
            case "left": node.Left = Number(node, name, value); break;
            case "right": node.Right = Number(node, name, value); break;
            case "top": node.Top = Number(node, name, value); break;
            case "bottom": node.Bottom = Number(node, name, value); break;
            case "z-index": node.ZIndex = Number(node, name, value, int.MinValue) ?? 0; break;
            case "pointer-events": node.PointerEvents = value is null || Boolean(node, name, value); break;
            case "onpointerdown": node.PointerDownHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onpointerup": node.PointerUpHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onpointermove": node.PointerMoveHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onpointerenter": node.PointerEnterHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onpointerleave": node.PointerLeaveHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onclick": node.ClickHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onwheel": node.WheelHandlerId = EventHandler(node, name, value, handlerId); break;
            case "shared-columns": node.SharedColumns = Boolean(node, name, value); break;
            case "cross-alignment":
                node.CrossAlignment = value is null ? TuiCrossAlignment.Stretch
                    : Enum.TryParse<TuiCrossAlignment>(value.ToString(), true, out var alignment) && Enum.IsDefined(alignment)
                        ? alignment : throw InvalidAttribute(node, name, value, "stretch, start, center, or end");
                break;
            case "wrap-mode":
                node.WrapMode = value is null ? NativeTextWrapMode.Character
                    : Enum.TryParse<NativeTextWrapMode>(value.ToString(), true, out var wrap) && Enum.IsDefined(wrap)
                        ? wrap : throw InvalidAttribute(node, name, value, "none, character, or word");
                break;
            case "tail": node.Tail = Boolean(node, name, value); break;
            case "selectable": node.Selectable = Boolean(node, name, value); break;
            case "scroll": node.Scroll = Number(node, name, value) ?? 0; break;
            case "cursor": node.Cursor = Number(node, name, value); break;
            case "selection-anchor": node.SelectionAnchor = Number(node, name, value); break;
            case "max-height": node.MaxHeight = Number(node, name, value, 1) ?? 1; break;
            case "text-max-height": node.TextMaxHeight = Number(node, name, value); break;
            case "focus-key": node.FocusKey = value?.ToString(); break;
            case "onkeydown": node.KeyHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onsizechanged": node.SizeHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onpaste": node.PasteHandlerId = EventHandler(node, name, value, handlerId); break;
            case "ontextinput": node.TextInputHandlerId = EventHandler(node, name, value, handlerId); break;
            case "onclose": node.CloseHandlerId = EventHandler(node, name, value, handlerId); break;
            case "placeholder": node.Placeholder = value?.ToString(); break;
            case "placeholder-fg": node.PlaceholderFg = Color(node, name, value); break;
            case "cursor-color": node.CursorColor = Color(node, name, value); break;
            case "selection-fg": node.SelectionForeground = Color(node, name, value); break;
            case "selection-bg": node.SelectionBackground = Color(node, name, value); break;
            case "bold": node.Bold = Boolean(node, name, value); break;
            case "dim": node.Dim = Boolean(node, name, value); break;
            case "fg": node.Fg = Color(node, name, value); break;
            case "bg": node.Bg = Color(node, name, value); break;
            case "padding-x": node.PaddingX = Number(node, name, value) ?? 0; break;
            case "padding-left": node.PaddingLeftOverride = Number(node, name, value); break;
            case "padding-right": node.PaddingRightOverride = Number(node, name, value); break;
            case "padding-top": node.PaddingTop = Number(node, name, value) ?? 0; break;
            case "padding-bottom": node.PaddingBottom = Number(node, name, value) ?? 0; break;
            case "padding":
                node.PaddingX = node.PaddingTop = node.PaddingBottom = Number(node, name, value) ?? 0;
                break;
        }
    }

    // RenderTreeBuilder converts non-component element attributes to strings,
    // except booleans and event handlers. Component parameters retain their types.
    private static int? Number(TuiNode node, string name, object? value, int minimum = 0)
    {
        if (value is null) return null;
        var number = value switch
        {
            int integer => integer,
            byte integer => integer,
            short integer => integer,
            ushort integer => integer,
            long integer when integer is >= int.MinValue and <= int.MaxValue => (int)integer,
            uint integer when integer <= int.MaxValue => (int)integer,
            string text when int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw InvalidAttribute(node, name, value, $"an invariant integer in [{minimum}, {int.MaxValue}]")
        };
        if (number < minimum) throw InvalidAttribute(node, name, value, $"an integer in [{minimum}, {int.MaxValue}]");
        return number;
    }

    private static bool Boolean(TuiNode node, string name, object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text when bool.TryParse(text, out var boolean) => boolean,
        _ => throw InvalidAttribute(node, name, value, "true or false")
    };

    private static ulong EventHandler(TuiNode node, string name, object? value, ulong handlerId) => value is null ? 0
        : handlerId != 0 ? handlerId : throw InvalidAttribute(node, name, value, "a Blazor event callback, not a string-valued event");

#pragma warning disable MA0015 // The message identifies the actual markup attribute; a C# helper parameter suffix is misleading.
    private static ArgumentException InvalidAttribute(TuiNode node, string name, object value, string expected) =>
        new($"Element '{node.TagName}' attribute '{name}' requires {expected}; received '{value}' ({value.GetType().Name}).");
#pragma warning restore MA0015

    private bool IsShutdownCancellation(Exception exception) => _eventsStopped && _stoppingToken.IsCancellationRequested
        && exception is OperationCanceledException cancellation && cancellation.CancellationToken == _stoppingToken;

    protected override void HandleException(Exception exception)
    {
        if (!IsShutdownCancellation(exception)) Error ??= exception;
    }

    protected override void Dispose(bool disposing)
    {
        try { if (disposing) RootNode.ReleaseNativeText(); }
        finally { base.Dispose(disposing); }
    }
}
