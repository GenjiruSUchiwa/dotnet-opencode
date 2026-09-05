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
        && catalog.Location == SelectionLocation ? catalog
        : _presentation is { } presentation && presentation.Location == SelectionLocation ? presentation.Skills : null;

    private async Task OpenSkills()
    {
        if (RequireSessionClient is null) return;
        CloseDialog();
        try
        {
            _skillClient = await RequireSessionClient(_configurationLifetime.Token);
            _skillLocation = SelectionLocation;
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
        var range = PromptInsertionRange();
        AttachSkill(selection, "@" + selection.Skill.Id.Value, range.Start, range.Length);
        return Task.CompletedTask;
    }

    private void AttachSkill(SkillSelection selection, string label, int start, int length)
    {
        if (selection.Location != SelectionLocation)
            throw new InvalidOperationException("The skill belongs to a different location.");
        if ((CapturePromptInput(_input).Skills ?? []).Any(skill => skill.Id == selection.Skill.Id)) return;
        if (!ReplacePromptRange(start, length, label + " ")) throw new InvalidOperationException(_inputError ?? "Could not insert the skill mention.");
        AddPromptMark(start, label, new(AttachmentKind.Skill, 0, label, Skill: selection.ToAttachment()));
        _dismissedReferenceText = _input;
        _dismissedCommandInput = _input;
        _dirty = true;
    }

    private bool HasAttachedSkillSlash => Commands.SlashHead.Parse(_input) is { } head && (CapturePromptInput(_input).Skills ?? [])
        .Any(skill => skill.Id.Value == head.Name && skill.Mention is { Start: 0 } mention && mention.Text == "/" + head.Name);
}
