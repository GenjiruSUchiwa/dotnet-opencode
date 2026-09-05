namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Forms;
using OpenCode.Schema;
using OpenTui.Blazor.Forms;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionFormSnapshot?>? ReadForms { get; set; }
    [Parameter] public Func<FormReplyRequest, CancellationToken, Task>? ReplyForm { get; set; }
    [Parameter] public Func<FormCancelRequest, CancellationToken, Task>? CancelForm { get; set; }
    [Parameter] public Func<CancellationToken, Task>? RefreshForms { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? OpenFormLink { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? CopyFormLink { get; set; }
    [Parameter] public Func<CancellationToken, Task<string?>>? ReadFormClipboard { get; set; }
    private SessionFormSnapshot? _formScope;
    private bool _refreshingForms;
    private string? _formRefreshError;

    private PendingForm? ActiveForm => _formScope is { } scope && scope.Session == _sessionId
        ? FormAdapter.FirstForRoute(scope.Pending, scope.Location, _sessionId, scope.Descendants, _childSession) : null;
    private bool PromptBlocked => ActivePermission is not null || ActiveForm is not null;
    private int FormWidth => Math.Max(1, _hasConversation ? ComposerWidth : _width - 4);

    private void ReadFormState()
    {
        var snapshot = ReadForms?.Invoke();
        if (ReferenceEquals(snapshot, _formScope)) return;
        _formScope = snapshot;
        _dirty = true;
        if (ActiveForm is null) return;
        _focusedPrompt = false;
        _keyDispatcher?.ClearPending();
        _keyHint = null;
        if (_palette || _models || _agents || _variants || _sessions || _tabList || _transcriptRowPicker || _settings) CloseDialog();
    }

    private Task SendFormReply(FormReplyRequest request, CancellationToken cancellationToken) => ReplyForm is not null
        ? ReplyForm(request, cancellationToken) : throw new InvalidOperationException("Form replies are not connected to the server.");

    private Task SendFormCancellation(FormCancelRequest request, CancellationToken cancellationToken) => CancelForm is not null
        ? CancelForm(request, cancellationToken) : throw new InvalidOperationException("Form cancellation is not connected to the server.");

    private async Task RefreshFormState()
    {
        if (_refreshingForms || RefreshForms is null || _configurationLifetime.IsCancellationRequested) return;
        _refreshingForms = true;
        _formRefreshError = null;
        try { await RefreshForms(_configurationLifetime.Token); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _formRefreshError = SessionClientAdapter.Describe(exception); }
        finally { _refreshingForms = false; ReadFormState(); _dirty = true; }
    }
}
