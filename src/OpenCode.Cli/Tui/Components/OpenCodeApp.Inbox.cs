namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Schema;

public enum PendingInputAction { Steer, Queue, Cancel }

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionId, MessageId, PendingInputAction, CancellationToken, Task>? MutateInbox { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task>? RefreshInbox { get; set; }
    private bool _inboxDialog;
    private bool _queuedOnly;
    private SessionId? _inboxOwner;
    private MessageId? _pendingSteer;
    private readonly HashSet<(SessionId Session, MessageId Item)> _inboxMutations = [];
    private string? _inboxRefreshError;
    private string? _inboxActionError;

    private IReadOnlyList<SessionInboxItem> PendingInputs => _sessionId is { } id
        ? (ReadSessionObservation?.Invoke(id)?.Inbox ?? []).Where(item => item.Payload is UserInboxPayload).ToArray() : [];
    private IReadOnlyList<SessionInboxItem> QueuedInputs => PendingInputs.Where(item => item.Delivery == InboxDeliveryMode.Queue).ToArray();
    private IReadOnlyList<SessionInboxItem> DialogInbox => _inboxOwner is { } id
        ? (ReadSessionObservation?.Invoke(id)?.Inbox ?? []).Where(item => item.Payload is UserInboxPayload
            && (!_queuedOnly || item.Delivery == InboxDeliveryMode.Queue)).ToArray() : [];

    private IReadOnlyList<DialogSelectOption<MessageId?>> InboxOptions() => DialogInbox.Select((item, index) =>
        new DialogSelectOption<MessageId?>(item.Id, ((UserInboxPayload)item.Payload).Text,
            Description: _queuedOnly ? null : item.Delivery == InboxDeliveryMode.Queue ? "Queued" : "Pending steer",
            Footer: $"{index + 1} of {DialogInbox.Count}", Disabled: _inboxMutations.Contains((item.SessionId, item.Id)))).ToArray();

    private IReadOnlyList<DialogSelectAction<MessageId?>> InboxActions => MutateInbox is null ? [] :
        [new("queued_prompt.delete", "delete", Shortcut("queued_prompt.delete"), async option =>
        {
            if (option?.Value is not { } itemId || _inboxOwner is not { } owner) return;
            var last = DialogInbox.Count == 1;
            if (await ChangePendingInput(owner, itemId, PendingInputAction.Cancel) && last) CloseDialog();
        })];

    private Task OpenQueuedPrompts() => OpenPendingInputs(true);
    private Task OpenPendingInputs(bool queuedOnly)
    {
        if (_sessionId is not { } owner || MutateInbox is null) return Task.CompletedTask;
        CloseDialog();
        _inboxOwner = owner;
        _inboxActionError = null;
        _queuedOnly = queuedOnly;
        _inboxDialog = true;
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }

    private async Task SelectPendingInput(MessageId? value)
    {
        if (value is not { } id) return;
        if (_inboxOwner is not { } owner || DialogInbox.FirstOrDefault(item => item.Id == id) is not { } item) return;
        if (item.Delivery == InboxDeliveryMode.Steer)
        {
            _pendingSteer = id;
            _dirty = true;
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (await ChangePendingInput(owner, id, PendingInputAction.Steer)) CloseDialog();
    }

    private async Task ChangePendingSteer(PendingInputAction action)
    {
        if (_inboxOwner is not { } owner || _pendingSteer is not { } id) return;
        if (await ChangePendingInput(owner, id, action)) CloseDialog();
    }

    private async Task<bool> ChangePendingInput(SessionId owner, MessageId id, PendingInputAction action)
    {
        if (MutateInbox is null || !_inboxMutations.Add((owner, id))) return false;
        _inboxActionError = null;
        _dirty = true;
        try
        {
            if (!(ReadSessionObservation?.Invoke(owner)?.Inbox ?? []).Any(item => item.Id == id && item.Payload is UserInboxPayload))
                throw new InvalidOperationException("The input is no longer pending.");
            await MutateInbox(owner, id, action, _configurationLifetime.Token);
            return true;
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            _inboxActionError = $"Failed to {(action == PendingInputAction.Cancel ? "delete" : action.ToString().ToLowerInvariant())} pending prompt: {SessionClientAdapter.Describe(exception)}";
            _inputError = _inboxActionError;
            return false;
        }
        finally
        {
            _inboxMutations.Remove((owner, id));
            if (RefreshInbox is not null) _keyTasks.Add(RefreshPendingInputs(owner));
            _dirty = true;
        }
    }

    private async Task RefreshPendingInputs(SessionId owner)
    {
        try { await RefreshInbox!(owner, _configurationLifetime.Token); _inboxRefreshError = null; }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _inboxRefreshError = $"Could not refresh pending inputs: {SessionClientAdapter.Describe(exception)}";
        }
        _dirty = true;
    }

    private bool PromoteFirstQueued()
    {
        if (_sessionId is not { } owner || MutateInbox is null || QueuedInputs.FirstOrDefault() is not { } item) return false;
        _keyTasks.Add(ChangePendingInput(owner, item.Id, PendingInputAction.Steer));
        return true;
    }
}
