namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.MessageActions;
using OpenCode.Cli.Tui.Transcript;
using OpenCode.Schema;
using OpenTui.Blazor;

public partial class OpenCodeApp
{
    [Parameter] public ITextClipboard? Clipboard { get; set; }
    [Parameter] public Func<MessageTarget, CancellationToken, Task<SessionRevert>>? StageMessageRevert { get; set; }
    [Parameter] public Func<MessageTarget, CancellationToken, Task>? ReconcileMessageTarget { get; set; }
    [Parameter] public Func<MessageTarget, CancellationToken, Task<SessionInfo>>? ForkBeforeMessage { get; set; }
    private MessageActionsController? _messageActions;
    private MessageTarget? _messageTarget;
    private MarkdownPresentation _markdownMode;
    private readonly Dictionary<Guid, PromptInput> _promptParts = [];
    private bool _copyingPrompt;

    private void ConnectMessageActions()
    {
        if (Clipboard is null) return;
        _messageActions = new(Clipboard,
            target => target.SessionId == _sessionId ? _transcriptMessages.FirstOrDefault(message => message.Id == target.MessageId) : null,
            new MessageActionServices(error => { _inputError = SessionClientAdapter.Describe(error); _dirty = true; })
            {
                Jump = target => { TranscriptScroll.Reveal(target.MessageId.Value); _dirty = true; return Task.CompletedTask; },
                RestorePrompt = RestoreProjectedUserPrompt,
                StageRevert = StageMessageRevert,
                Reconcile = ReloadConfiguration is null && ReconcileMessageTarget is null ? null : ReconcileMessageSession,
                ForkBefore = OpenTabSession is null ? null : ForkBeforeMessage,
                OpenFork = OpenTabSession is null || ForkBeforeMessage is null ? null : OpenForkSession,
                CanMutate = target => target.SessionId == _sessionId && NavigationReady && !PromptBlocked
            });
    }

    private async Task ReconcileMessageSession(MessageTarget target, CancellationToken cancellationToken)
    {
        if (ReconcileMessageTarget is not null) { await ReconcileMessageTarget(target, cancellationToken); return; }
        if (_sessionId != target.SessionId || ReloadConfiguration is null) return;
        var configuration = await ReloadConfiguration(cancellationToken);
        if (_sessionId == target.SessionId && configuration.SessionId == target.SessionId) ApplyConfiguration(configuration);
    }

    private async Task OpenForkSession(SessionInfo fork, UserMessage original)
    {
        try
        {
            await RunTabNavigation(async token =>
            {
                var configuration = await OpenTabSession!(fork.Id, token);
                if (configuration.SessionId != fork.Id) throw new InvalidOperationException("Fork hydration returned a different session.");
                var before = _tabs.Persisted;
                CaptureTab();
                _tabs = _tabs.Open(fork.Id, configuration.SessionTitle ?? fork.Title ?? "Forked session");
                RestoreTab(_tabs.Current, configuration);
                RestoreProjectedUserPrompt(original);
                QueueTabWrite(before, _tabs.Persisted);
                UpdateViewedTab();
            }, throwErrors: true, ct: CancellationToken.None);
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"The session was forked, but opening it failed: {SessionClientAdapter.Describe(exception)}", exception);
        }
    }

    private Task OpenMessageActions(MessageId id)
    {
        if (_sessionId is not { } session || _messageActions is null) return Task.CompletedTask;
        CloseDialog();
        _messageTarget = new(session, id);
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }

    private async Task CopyCode(string code)
    {
        if (_messageActions is not null) await _messageActions.CopyCodeAsync(code, _configurationLifetime.Token);
    }

    private void RestoreProjectedUserPrompt(UserMessage message)
    {
        _editHistory.Record(CurrentEdit);
        _promptParts[EditorKey] = new(message.Text,
            message.Files?.Select(file => new PromptInputFileAttachment(file.Source is PromptUriFileSource uri
                ? uri.Uri : $"data:{file.Mime};base64,{file.Data}", file.Name, file.Description, file.Mention)).ToArray(),
            message.Agents?.ToArray(), message.Skills?.Select(skill => new PromptInputSkillAttachment(skill.Id, skill.Mention)).ToArray());
        RememberPromptMetadata(EditorKey, message.Metadata);
        foreach (var file in message.Files ?? [])
            _attachmentKinds[file.Source is PromptUriFileSource uri ? uri.Uri : $"data:{file.Mime};base64,{file.Data}"] =
                file.Mime == "application/x-directory" ? Attachments.AttachmentKind.Directory : Attachments.AttachmentKind.File;
        _input = message.Text;
        _cursor = _input.Length;
        _selectionAnchor = null;
        _preferredColumn = null;
        _draftRevision++;
        _dirty = true;
    }

    // Shared with the admission owner: the canonical unprepared prompt is captured
    // before clearing the origin draft, without minting a second message/inbox ID.
    private PromptInput CapturePromptInput(string text) => _promptParts.TryGetValue(EditorKey, out var parts)
        ? parts.Text == text ? parts : parts with { Text = text } : new(text);
    private void ClearPromptAttachments(Guid tab)
    {
        _promptParts.Remove(tab);
        if (_promptMarkStates.TryGetValue(tab, out var state)) state.Clear();
    }
    private void RestorePromptAttachments(Guid tab, PromptInput prompt)
    {
        _promptParts[tab] = prompt;
        if (_promptMarkStates.TryGetValue(tab, out var state)) state.Reconcile(prompt);
    }
    private bool HasPromptAttachments => _promptParts.TryGetValue(EditorKey, out var parts)
        && ((parts.Files?.Count ?? 0) + (parts.Agents?.Count ?? 0) + (parts.Skills?.Count ?? 0) > 0);

    private async Task CopyPromptSelection()
    {
        if (_copyingPrompt || !HasSelection) return;
        if (Clipboard is null) { _inputError = "Clipboard writing is unavailable."; _dirty = true; return; }
        _copyingPrompt = true;
        var text = _input;
        var cursor = _cursor;
        var anchor = _selectionAnchor!.Value;
        var editor = EditorKey;
        try
        {
            await Clipboard.WriteTextAsync(text[Math.Min(cursor, anchor)..Math.Max(cursor, anchor)], _configurationLifetime.Token);
            if (EditorKey == editor && _input == text && _cursor == cursor && _selectionAnchor == anchor) _selectionAnchor = null;
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _inputError = SessionClientAdapter.Describe(exception); }
        finally { _copyingPrompt = false; _dirty = true; }
    }
}
