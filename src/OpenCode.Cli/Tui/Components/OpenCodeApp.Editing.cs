namespace OpenCode.Cli.Tui.Components;

using OpenTui.Blazor;
using System.Runtime.CompilerServices;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenTui.Blazor.TextMarks;

public partial class OpenCodeApp
{
    private int? _selectionAnchor;
    private long _draftRevision;
    private string? _inputError;
    private TerminalEditHistory _editHistory = new();
    private readonly ConditionalWeakTable<TerminalEditHistory.Snapshot, PromptEditDocument> _editDocuments = new();
    private readonly Dictionary<(Guid Tab, int Index), PromptEditDocument> _historyDocuments = [];
    private readonly Dictionary<Guid, PromptEditDocument> _historyDrafts = [];
    private TerminalEditHistory.Snapshot CurrentEdit
    {
        get
        {
            var snapshot = new TerminalEditHistory.Snapshot(_input, _cursor, _selectionAnchor);
            var input = CapturePromptAdmission(_input);
            _editDocuments.Add(snapshot, new(new(input.Text, input.Files, input.Agents, input.Skills), input.Metadata, GetPromptMarks().Snapshot(), ShellMode));
            return snapshot;
        }
    }

    private bool RestoreEdit(bool redo)
    {
        var value = redo ? _editHistory.Redo(CurrentEdit) : _editHistory.Undo(CurrentEdit);
        if (value is null) return false;
        _input = value.Text;
        _cursor = value.Cursor;
        _selectionAnchor = value.SelectionAnchor;
        RestoreEditDocument(_editDocuments.TryGetValue(value, out var document) ? document : null);
        _shellModes[_tabs.Selected] = document?.ShellMode == true;
        _preferredColumn = null;
        _highSurrogate = null;
        _inputError = null;
        _draftRevision++;
        _dirty = true;
        return true;
    }
    private bool HasSelection => _selectionAnchor is { } anchor && anchor != _cursor;

    private void MoveCursor(int target, bool select, TerminalMarkMotion motion = TerminalMarkMotion.Set)
    {
        target = Math.Clamp(target, 0, _input.Length);
        try { target = MarkedCursorTarget(target, select || HasSelection, motion); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { _inputError = exception.Message; _dirty = true; return; }
        if (select) _selectionAnchor ??= _cursor;
        else _selectionAnchor = null;
        _cursor = Math.Clamp(target, 0, _input.Length);
        _dirty = true;
    }

    private bool RemoveSelection()
    {
        if (!HasSelection) { _selectionAnchor = null; return false; }
        var start = Math.Min(_selectionAnchor!.Value, _cursor);
        var end = Math.Max(_selectionAnchor.Value, _cursor);
        return ReplacePromptRange(start, end - start, "", record: false);
    }

    private void InsertText(string text)
    {
        if (text.Length == 0) return;
        var start = HasSelection ? Math.Min(_cursor, _selectionAnchor!.Value) : _cursor;
        var length = HasSelection ? Math.Abs(_cursor - _selectionAnchor!.Value) : 0;
        ReplacePromptRange(start, length, text);
    }

    private bool ReplacePromptRange(int start, int length, string text, bool record = true)
    {
        AttachmentTextMarks next;
        try { next = GetPromptMarks().Replace(start, length, text, MeasureMentionElement); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { _inputError = exception.Message; _dirty = true; return false; }
        if (record) _editHistory.Record(CurrentEdit);
        _input = next.Input.Text;
        _promptParts[_tabs.Selected] = next.Input;
        _promptMarkStates[_tabs.Selected] = next;
        _cursor = start + text.Length;
        _selectionAnchor = null;
        _preferredColumn = null;
        _inputError = null;
        _draftRevision++;
        _dirty = true;
        return true;
    }

    private int MeasureMentionElement(string text) => _measure is not null
        ? _measure(text, int.MaxValue).Position(text.Length).Column
        : _promptMarkMetrics?.Measure(text)
            ?? throw new InvalidOperationException("Prompt text metrics are not ready. Wait for prompt layout before editing an attachment label.");

    private void RestoreEditDocument(PromptEditDocument? document)
    {
        if (document is null) ClearPromptAttachments(_tabs.Selected);
        else RestorePromptAttachments(_tabs.Selected, document.Input);
        if (document?.Marks is { } marks) _promptMarkStates[_tabs.Selected] = AttachmentTextMarks.Restore(document.Input, marks);
        RememberPromptMetadata(_tabs.Selected, document?.Metadata);
    }

    private void RememberPromptHistory(SessionPromptInput input) => _historyDocuments[(_tabs.Selected, _history.Count)] =
        new(new(input.Text, input.Files, input.Agents, input.Skills), input.Metadata, GetPromptMarks().Snapshot());

    private void InputText(TerminalTextInputEventArgs args)
    {
        args.Handled = true;
        if (!PromptBlocked && !EnterShellMode(args.Text)) InsertText(args.Text);
    }

}
