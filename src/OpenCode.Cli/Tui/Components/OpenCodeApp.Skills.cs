namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Cli.Tui.Skills;
using OpenCode.Cli.Tui.Layout;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    private bool _skillsOpen;
    private SessionHttpClient? _skillClient;
    private LocationRef? _skillLocation;
    private SkillCatalogSnapshot? _skillCatalog;
    private SessionPresentation? _skillPresentation;
    private SkillCatalogSnapshot? ActiveSkills => _skillCatalog is { } catalog && _skillClient == ReadSessionClient?.Invoke()
        && ReferenceEquals(_skillPresentation, _presentation)
        && catalog.Location == (_presentation?.Location ?? new LocationRef(CurrentDirectory)) ? catalog : _presentation?.Skills;

    private async Task OpenSkills()
    {
        if (RequireSessionClient is null) return;
        CloseDialog();
        try
        {
            _skillClient = await RequireSessionClient(_configurationLifetime.Token);
            _skillLocation = _presentation?.Location ?? new LocationRef(CurrentDirectory);
            _skillsOpen = true;
            _dirty = true;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _inputError = SessionClientAdapter.Describe(exception); _dirty = true; }
    }

    private void CacheSkills(SkillCatalogSnapshot catalog) { _skillCatalog = catalog; _skillPresentation = _presentation; _dirty = true; }
    private Task SelectSkill(SkillSelection selection)
    {
        AttachSkill(selection, "@" + selection.Skill.Id.Value, HasSelection ? Math.Min(_cursor, _selectionAnchor!.Value) : _cursor,
            HasSelection ? Math.Abs(_cursor - _selectionAnchor!.Value) : 0);
        return Task.CompletedTask;
    }

    private void AttachSkill(SkillSelection selection, string label, int start, int length)
    {
        if (selection.Location != (_presentation?.Location ?? new LocationRef(CurrentDirectory)))
            throw new InvalidOperationException("The skill belongs to a different location.");
        if ((CapturePromptInput(_input).Skills ?? []).Any(skill => skill.Id == selection.Skill.Id)) return;
        var offset = AttachmentEdits.Offset(_input, start, MeasureMentionElement);
        var end = offset + AttachmentEdits.Offset(label, label.Length, MeasureMentionElement);
        if (!ReplacePromptRange(start, length, label + " ")) throw new InvalidOperationException(_inputError ?? "Could not insert the skill mention.");
        var input = CapturePromptInput(_input);
        RestorePromptAttachments(_tabs.Selected, input with
        { Skills = (input.Skills ?? []).Append(selection.ToAttachment(new(offset, end, label))).ToArray() });
        _dismissedReferenceText = _input;
        _dismissedCommandInput = _input;
        _dirty = true;
    }

    private bool HasAttachedSkillSlash => Commands.SlashHead.Parse(_input) is { } head && (CapturePromptInput(_input).Skills ?? [])
        .Any(skill => skill.Id.Value == head.Name && skill.Mention is { Start: 0 } mention && mention.Text == "/" + head.Name);
}
