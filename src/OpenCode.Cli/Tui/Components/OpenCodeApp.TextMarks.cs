namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Cli.Tui.Theme;
using OpenCode.Schema;
using OpenTui.Blazor.TextMarks;

public partial class OpenCodeApp
{
    // The host sets this only after its actual Input renderer supports TextMarks.
    [Parameter] public bool InlineTextMarksSupported { get; set; }
    [Parameter] public Func<TerminalTextMarkMetrics?>? ReadPromptMarkMetrics { get; set; }
    private TerminalTextMarkMetrics? _promptMarkMetrics;
    private readonly Dictionary<Guid, AttachmentTextMarks> _promptMarkStates = [];
    private IReadOnlyList<TerminalTextMark> PromptTextMarks => !InlineTextMarksSupported || _measure is null && _promptMarkMetrics is null ? []
        : GetPromptMarks().Project(MeasureMentionElement, PromptMarkStyle);

    private void ReadTextMarkMetrics()
    {
        if (ReadPromptMarkMetrics?.Invoke() is not { } metrics || metrics == _promptMarkMetrics) return;
        _promptMarkMetrics = metrics;
        _dirty = true;
    }

    private AttachmentTextMarks GetPromptMarks()
    {
        var input = CapturePromptInput(_input);
        if (!_promptMarkStates.TryGetValue(_tabs.Selected, out var state))
            _promptMarkStates[_tabs.Selected] = state = new(input);
        else if (!ReferenceEquals(state.Input, input)) state.Reconcile(input);
        return state;
    }

    private TerminalTextMarkStyle PromptMarkStyle(AttachmentKind kind, string? styleKey)
    {
        var scope = styleKey ?? (kind switch { AttachmentKind.Agent => "extmark.agent", AttachmentKind.Skill => "extmark.skill", _ => "extmark.file" });
        var style = ThemeSyntax.GenerateNative(BaseColors).First(rule => rule.Scopes.Contains(scope));
        return new(style.Foreground, style.Background, style.Attributes);
    }

    private int MarkedCursorTarget(int target, bool selection, TerminalMarkMotion motion)
    {
        var state = GetPromptMarks();
        if (selection || motion == TerminalMarkMotion.Direct || state.Marks.All.Count == 0) return target;
        var map = new TerminalTextMap(_input, MeasureMentionElement);
        return map.Utf16AtDisplay(state.Marks.MoveCursor(map.DisplayAtUtf16(_cursor), map.DisplayAtUtf16(target), motion, false));
    }

    private TerminalMarkDeletion? AtomicPromptDeletion(bool backward)
    {
        var state = GetPromptMarks();
        if (HasSelection || state.Marks.All.Count == 0) return null;
        var map = new TerminalTextMap(_input, MeasureMentionElement);
        var range = state.Marks.AtomicDeletion(map.DisplayAtUtf16(_cursor), backward, false);
        if (range is null) return null;
        var start = map.Utf16AtDisplay(range.Value.Start);
        return new(start, map.Utf16AtDisplay(range.Value.Start + range.Value.Length, roundUp: true) - start);
    }
}
