namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using System.Collections.Immutable;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Keymap;

public partial class OpenCodeApp
{
    [Parameter] public Func<string, bool>? DispatchPromptPaste { get; set; }
    private int? _selectionAnchor;
    private long _draftRevision;
    private string? _inputError;
    private readonly Dictionary<Guid, PromptEditDocument> _editorDocuments = [];
    private readonly Dictionary<(Guid Tab, int Index), PromptEditDocument> _historyDocuments = [];
    private readonly Dictionary<Guid, PromptEditDocument> _historyDrafts = [];
    private TextareaState? _nativePrompt;
    private Guid? _nativePromptOwner;
    private long _nativeRevision = -1;
    private bool _restoringNative;
    private bool _nativeRestoreFailed;
    private TextareaState? ActivePrompt => !_nativeRestoreFailed && _nativePromptOwner == EditorKey && _nativePrompt is { IsMounted: true, IsDisposed: false } state ? state : null;
    private bool HasSelection => ActivePrompt?.Snapshot.Selection is { } range && range.End > range.Start;

    private void PromptReady(Guid owner, TextareaState state)
    {
        _nativePrompt = state;
        _nativePromptOwner = owner;
        _nativeRevision = -1;
        _nativeRestoreFailed = false;
        state.Bindings.Clear(); // Configured CLI bindings delegate to Execute; native default text insertion still owns typing.
        state.Marks.RegisterType("prompt-part");
        var document = _editorDocuments.GetValueOrDefault(owner) ?? new PromptEditDocument(_promptParts.GetValueOrDefault(owner) ?? new(""),
            _promptMetadata.GetValueOrDefault(owner), ShellMode: _shellModes.GetValueOrDefault(owner));
        try { RestorePromptDocument(owner, document with { ShellMode = _shellModes.GetValueOrDefault(owner) }); }
        catch (Exception exception) { _nativeRestoreFailed = true; _inputError = "The native prompt could not restore its document: " + exception.Message; _dirty = true; }
    }

    private void PromptChanged(Guid owner, TextareaState state, TextareaChangeEventArgs args)
    {
        if (!args.IsCurrent || _restoringNative || _nativeRestoreFailed || !ReferenceEquals(state, _nativePrompt) || owner != _nativePromptOwner) return;
        if (owner == EditorKey && args.Before.Text != args.After.Text) _inputError = null;
        ObservePrompt(owner, state, args.Document);
    }

    private void PromptSubmitted(Guid owner, TextareaState state, TextareaSubmitEventArgs args)
    {
        if (owner != EditorKey || !ReferenceEquals(state, ActivePrompt)) return;
        ObservePrompt(owner, state, args.Document);
        SubmitPrompt();
    }

    private void PromptUnmounted(Guid owner, TextareaState state)
    {
        if (!ReferenceEquals(_nativePrompt, state)) return;
        if (!_nativeRestoreFailed && state.IsMounted && !state.IsDisposed) ObservePrompt(owner, state);
        _nativePrompt = null;
        _nativePromptOwner = null;
        _nativeRevision = -1;
    }

    private void ObservePrompt(Guid owner, TextareaState state, TextareaDocument? snapshot = null)
    {
        if (_restoringNative || _nativeRestoreFailed || state.IsDisposed || !state.IsMounted) return;
        if (ReferenceEquals(state, _nativePrompt) && _nativeRevision == state.Snapshot.Revision)
        {
            if (_editorDocuments.TryGetValue(owner, out var previous) && previous.ShellMode != _shellModes.GetValueOrDefault(owner))
                _editorDocuments[owner] = previous with { ShellMode = _shellModes.GetValueOrDefault(owner) };
            return;
        }
        var basis = _editorDocuments.GetValueOrDefault(owner) ?? new PromptEditDocument(new(""), _promptMetadata.GetValueOrDefault(owner));
        var document = PromptDocumentAdapter.Capture(snapshot ?? state.CaptureDocument(), basis, _shellModes.GetValueOrDefault(owner));
        _editorDocuments[owner] = document;
        _promptParts[owner] = document.Input;
        if (ReferenceEquals(state, _nativePrompt)) _nativeRevision = state.Snapshot.Revision;
        if (owner != EditorKey) return;
        ProjectPrompt(document);
    }

    private void ProjectPrompt(PromptEditDocument document)
    {
        _input = document.Input.Text;
        _cursor = document.Editor?.CursorUtf16 ?? _input.Length;
        _selectionAnchor = document.Editor?.AnchorUtf16;
        _draftRevision++;
        _dirty = true;
    }

    private PromptEditDocument CapturePromptDocument()
    {
        if (ActivePrompt is { } state) ObservePrompt(EditorKey, state);
        var document = _editorDocuments.GetValueOrDefault(EditorKey) ?? new PromptEditDocument(CapturePromptInput(_input), _promptMetadata.GetValueOrDefault(EditorKey));
        return _editorDocuments[EditorKey] = PromptDocumentAdapter.Prepare(document with { Metadata = _promptMetadata.GetValueOrDefault(EditorKey), ShellMode = ShellMode });
    }

    private void RestorePromptDocument(Guid owner, PromptEditDocument document, bool moveToEnd = false)
    {
        var prepared = PromptDocumentAdapter.Prepare(document, moveToEnd ? document.Input.Text.Length : null);
        if (_nativePromptOwner == owner && _nativePrompt is { IsMounted: true, IsDisposed: false } state)
        {
            _restoringNative = true;
            try { state.SetDocument(prepared.Editor!); }
            finally { _restoringNative = false; }
            _nativeRestoreFailed = false;
            _editorDocuments[owner] = prepared;
            _shellModes[owner] = prepared.ShellMode;
            RememberPromptMetadata(owner, prepared.Metadata);
            _nativeRevision = -1;
            ObservePrompt(owner, state);
            return;
        }
        _editorDocuments[owner] = prepared;
        _promptParts[owner] = prepared.Input;
        _shellModes[owner] = prepared.ShellMode;
        RememberPromptMetadata(owner, prepared.Metadata);
        if (owner == EditorKey) ProjectPrompt(prepared); // Cache only; the new Prompt restores it once in OnReady.
    }

    private void ResetPromptDocument()
    {
        var owner = EditorKey;
        var empty = PromptDocumentAdapter.Prepare(new(new(""), null, ShellMode: ShellMode));
        _editorDocuments[owner] = empty;
        _promptParts[owner] = empty.Input;
        RememberPromptMetadata(owner, null);
        if (ActivePrompt is { } state) { state.Clear(); _nativeRevision = -1; ObservePrompt(owner, state); }
        else ProjectPrompt(empty);
    }

    private bool EditorEmpty(Guid owner) => !_nativeRestoreFailed && _nativePromptOwner == owner && _nativePrompt is { IsMounted: true, IsDisposed: false } state
        ? state.Text.Length == 0 : (_editorDocuments.GetValueOrDefault(owner)?.Input.Text ?? _tabViews.GetValueOrDefault(owner)?.Input ?? "").Length == 0;

    private bool ReplacePromptRange(int start, int length, string text)
    {
        if (ActivePrompt is not { } state) { OnInputError("The native prompt is not ready."); return false; }
        try
        {
            if (length > 0) state.SelectUtf16(start, checked(start + length));
            else state.SetCursorUtf16(start);
            state.InsertText(text);
            ObservePrompt(EditorKey, state);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { OnInputError(exception.Message); return false; }
    }

    private (int Start, int Length) PromptInsertionRange()
    {
        var document = CapturePromptDocument().Editor!;
        return document.Selection is { } selection ? (selection.StartUtf16, selection.EndUtf16 - selection.StartUtf16)
            : (document.CursorUtf16, 0);
    }

    private void AddPromptMark(int start, string label, PromptAttachmentData payload)
    {
        var state = ActivePrompt ?? throw new InvalidOperationException("The native prompt is not ready.");
        var basis = CapturePromptDocument();
        var order = state.MarkData.Values.OfType<PromptAttachmentData>().Concat(basis.Unmarked).Select(part => part.Order).DefaultIfEmpty(-1).Max() + 1;
        state.CreateMark(start, checked(start + label.Length), true, state.Marks.RegisterType("prompt-part"), payload.Style,
            data: payload with { Order = order, Label = label });
        _nativeRevision = -1;
        ObservePrompt(EditorKey, state);
    }

    private void RememberPromptHistory(SessionPromptInput input) => _historyDocuments[(EditorKey, _history.Count)] =
        CapturePromptDocument() with { Input = PromptDocumentAdapter.Copy(new(input.Text, input.Files, input.Agents, input.Skills)), Metadata = input.Metadata };

    private void PromptKeyInput(TerminalRoutedKeyEventArgs args)
    {
        if (ActivePrompt is not { } state || args.Input.EventType != KeyEventType.Press) return;
        ObservePrompt(EditorKey, state);
        if (!args.Input.Ctrl && !args.Input.Meta && !args.Input.Super && !args.Input.Hyper && EnterShellMode(args.Input.Text))
        { args.PreventDefault(); args.StopPropagation(); }
    }

    private void PromptPasteInput(TerminalRoutedPasteEventArgs args)
    {
        if (PromptBlocked || PromptOverlayOpen) { args.PreventDefault(); return; }
        if (args.Text.Length != 0) return; // Native default normalizes and inserts exactly once.
        args.PreventDefault();
        _keyTasks.Add(PasteClipboard());
    }

    private void PromptFocusChanged(TerminalFocusEventArgs args) { if (args.Focused) _terminalFocused = false; _dirty = true; }
    private void PromptSizeChanged(TerminalSizeEventArgs args) { _composerWidth = args.Width; _dirty = true; }
}
