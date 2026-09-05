namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Cli.Tui.Commands;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;
using OpenTui.Blazor.Keymap;

public partial class OpenCodeApp
{
    [Parameter] public Func<LocationRef, FileSystemFindInput, CancellationToken, Task<LocationResponse<IReadOnlyList<FileSystemEntry>>>>? FindPromptFiles { get; set; }
    private readonly Dictionary<string, AttachmentKind> _attachmentKinds = [];
    private ReferenceQuery? _referenceQuery;
    private ComposerAnchor? _referenceAnchor;
    private (Guid Tab, LocationRef Location, string Query)? _referenceSearch;
    private LocationResponse<IReadOnlyList<FileSystemEntry>>? _referenceFiles;
    private IReadOnlyList<ReferenceOption> _referenceOptions = [];
    private CancellationTokenSource? _referenceOperation;
    private bool _referenceLoading;
    private string? _referenceError;
    private string? _dismissedReferenceText;
    private int _referenceIndex;
    private bool ReferenceAutocompleteVisible => _referenceQuery is not null && _referenceAnchor is not null && !PromptBlocked && !PromptOverlayOpen
        && !_configurationBusy && !ShellMode && !_activitiesOpen && !_terminalListOpen && !_terminalFocused;

    private void UpdateReferenceAutocomplete()
    {
        var query = _dismissedReferenceText == _input ? null : ReferenceQuery.Parse(_input, _cursor);
        var anchor = query is null ? null : ReadComposerAnchor?.Invoke();
        if (_referenceQuery != query || _referenceAnchor != anchor) _dirty = true;
        if (_referenceQuery?.Search != query?.Search) _referenceIndex = 0;
        _referenceQuery = query;
        _referenceAnchor = anchor;
        if (!ReferenceAutocompleteVisible || query is null)
        {
            _referenceOperation?.Cancel();
            _referenceSearch = null;
            _referenceFiles = null;
            return;
        }
        var key = (_tabs.Selected, _presentation?.Location ?? new LocationRef(CurrentDirectory), query.Path);
        if (_referenceSearch != key)
        {
            _referenceOperation?.Cancel();
            _referenceSearch = key;
            _referenceFiles = null;
            _referenceError = null;
            if (FindPromptFiles is not null)
            {
                _referenceLoading = true;
                var operation = CancellationTokenSource.CreateLinkedTokenSource(_configurationLifetime.Token);
                _referenceOperation = operation;
                _keyTasks.Add(FindReferences(key, operation));
            }
            else { _referenceLoading = false; _referenceError = "Filesystem search is unavailable."; }
        }
        BuildReferenceOptions(query);
    }

    private async Task FindReferences((Guid Tab, LocationRef Location, string Query) key, CancellationTokenSource operation)
    {
        try
        {
            var result = await FindPromptFiles!(key.Location, new FileSystemFindInput(key.Query, Limit: 20), operation.Token);
            if (_referenceSearch == key && !operation.IsCancellationRequested) _referenceFiles = result;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_referenceSearch == key) _referenceError = $"Could not search files: {SessionClientAdapter.Describe(exception)}";
        }
        finally
        {
            if (ReferenceEquals(_referenceOperation, operation))
            {
                _referenceOperation = null;
                _referenceLoading = false;
                if (_referenceQuery is { } query) BuildReferenceOptions(query);
                _dirty = true;
            }
            operation.Dispose();
        }
    }

    private void BuildReferenceOptions(ReferenceQuery query)
    {
        var agents = (_presentation?.Agents ?? []).Where(agent => !agent.Hidden && agent.Mode != AgentMode.Primary)
            .Select(agent => new ReferenceOption("@" + agent.Id.Value, AttachmentKind.Agent, agent.Id.Value))
            .Select(option => (Option: option, Score: DialogSearch.Score(query.Path, option.Label, null, null)))
            .Where(item => query.Path.Length == 0 || item.Score > 0).OrderByDescending(item => item.Score).Select(item => item.Option);
        var files = _referenceFiles is { } result ? result.Data.Select(entry => FileReference.Create(result.Location, entry, query)) : [];
        var skills = (ActiveSkills?.Mentions() ?? []).Select(skill => new ReferenceOption(skill.Display, AttachmentKind.Skill, skill.Selection.Skill.Id.Value, skill.Description))
            .Where(skill => query.Path.Length == 0 || DialogSearch.Score(query.Path, skill.Label, null, null) > 0);
        var options = skills.Concat(agents).Concat(files);
        _referenceOptions = (query.Search.Length == 0 ? options : options.Take(10)).ToArray();
        _referenceIndex = Math.Clamp(_referenceIndex, 0, Math.Max(0, _referenceOptions.Count - 1));
    }

    private bool HandleReferenceAutocomplete(ConsoleKeyInfo key)
    {
        UpdateReferenceAutocomplete();
        if (!ReferenceAutocompleteVisible || !_focusedPrompt || _keyHint is not null || _resolvedBindings is null) return false;
        var name = KeyName(key);
        if (name is null) return false;
        var stroke = new KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control), key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var command = new[] { "prompt.autocomplete.prev", "prompt.autocomplete.next", "prompt.autocomplete.hide",
            "prompt.autocomplete.select", "prompt.autocomplete.complete", "prompt.clear" }
            .FirstOrDefault(id => _resolvedBindings.Get(id).Any(binding => binding.Sequence.Count == 1 && binding.Sequence[0].Stroke == stroke));
        if (command is "prompt.autocomplete.hide" or "prompt.clear")
        {
            _dismissedReferenceText = _input;
            _referenceQuery = null;
            _dirty = true;
            return true;
        }
        if (command is "prompt.autocomplete.prev" or "prompt.autocomplete.next")
        {
            if (_referenceOptions.Count > 0) HighlightReference((_referenceIndex + _referenceOptions.Count
                + (command == "prompt.autocomplete.prev" ? -1 : 1)) % _referenceOptions.Count);
            return true;
        }
        if (command is not ("prompt.autocomplete.select" or "prompt.autocomplete.complete")) return false;
        SelectReference(_referenceIndex, command == "prompt.autocomplete.complete");
        return true;
    }

    private void HighlightReference(int index) { _referenceIndex = index; _dirty = true; }
    private void SelectReference(int index) => SelectReference(index, false);

    private void SelectReference(int index, bool expandDirectory)
    {
        if (_referenceQuery is not { } query || _referenceOptions.ElementAtOrDefault(index) is not { } option || PromptBlocked) return;
        if (option.Kind == AttachmentKind.Skill)
        {
            if (ActiveSkills is not { } catalog) return;
            AttachSkill(catalog.Select(SkillId.FromExisting(option.Key)), option.Label, query.Start, _cursor - query.Start);
            _referenceQuery = null;
            return;
        }
        if (expandDirectory && option.Kind == AttachmentKind.Directory)
        {
            ReplacePromptRange(query.Start, _cursor - query.Start, "@" + option.Label.TrimEnd('/', '\\') + "/");
            _referenceIndex = 0;
            return;
        }
        var label = option.Kind == AttachmentKind.Agent ? option.Label : "@" + option.Label;
        var append = _cursor >= _input.Length || _input[_cursor] != ' ' ? " " : "";
        try
        {
            // Compute native-display offsets before recording or changing the document.
            var start = AttachmentEdits.Offset(_input, query.Start, MeasureMentionElement);
            var end = start + AttachmentEdits.Offset(label, label.Length, MeasureMentionElement);
            if (!ReplacePromptRange(query.Start, _cursor - query.Start, label + append)) return;
            var mention = new PromptMention(start, end, label);
            var prompt = CapturePromptInput(_input);
            if (option.Kind == AttachmentKind.Agent)
                prompt = prompt with { Agents = (prompt.Agents ?? []).Append(new PromptAgentAttachment(option.Key, mention)).ToArray() };
            else
            {
                _attachmentKinds[option.Key] = option.Kind;
                var file = new PromptInputFileAttachment(option.Key, option.Label, option.Description, mention);
                prompt = prompt with { Files = (prompt.Files ?? []).Where(item => item.Uri != file.Uri).Append(file).ToArray() };
            }
            RestorePromptAttachments(_tabs.Selected, prompt);
            _dismissedReferenceText = _input;
            _referenceQuery = null;
            _dirty = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { _inputError = exception.Message; _dirty = true; }
    }

    private void RemoveAttachment(AttachmentTarget target)
    {
        if (PromptBlocked) return;
        var input = CapturePromptInput(_input);
        var mention = target.Kind switch
        {
            AttachmentKind.File => input.Files?.FirstOrDefault(file => file.Uri == target.Key)?.Mention,
            AttachmentKind.Agent => input.Agents?.FirstOrDefault(agent => agent.Name == target.Key)?.Mention,
            _ => input.Skills?.FirstOrDefault(skill => skill.Id.Value == target.Key)?.Mention
        };
        _editHistory.Record(CurrentEdit);
        if (mention is not null)
        {
            try
            {
                var start = AttachmentEdits.Index(_input, mention.Start, MeasureMentionElement);
                var end = AttachmentEdits.Index(_input, mention.End, MeasureMentionElement);
                var cursor = _cursor;
                var anchor = _selectionAnchor;
                if (!ReplacePromptRange(start, end - start, "", record: false)) return;
                _cursor = Remap(cursor);
                _selectionAnchor = anchor is { } selected ? Remap(selected) : null;
                int Remap(int position) => position <= start ? position : position >= end ? position - (end - start) : start;
                input = CapturePromptInput(_input);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { _inputError = exception.Message; _dirty = true; return; }
        }
        RestorePromptAttachments(_tabs.Selected, input with
        {
            Files = target.Kind == AttachmentKind.File ? input.Files?.Where(file => file.Uri != target.Key).ToArray() : input.Files,
            Agents = target.Kind == AttachmentKind.Agent ? input.Agents?.Where(agent => agent.Name != target.Key).ToArray() : input.Agents,
            Skills = target.Kind == AttachmentKind.Skill ? input.Skills?.Where(skill => skill.Id.Value != target.Key).ToArray() : input.Skills
        });
        _draftRevision++;
        _dirty = true;
    }
}
