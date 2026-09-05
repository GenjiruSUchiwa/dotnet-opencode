namespace OpenCode.Cli.Tui;

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using OpenCode.Client;
using OpenCode.Cli.Tui.Forms;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Protocol.Groups;
using OpenCode.Protocol;
using OpenCode.Schema;

public sealed partial class SessionClientAdapter
{
    private readonly Dictionary<(LocationRef Location, string Owner, FormId Id), PendingForm> _forms = [];
    private readonly Dictionary<(LocationRef Location, string Owner, FormId Id), long> _formVersions = [];
    private readonly Dictionary<SessionId, (SessionId? Parent, LocationRef Location)> _formSessions = [];
    private SessionFormTransport? _formTransport;
    private SessionFormSnapshot? _formSnapshot;
    private LocationRef? _homeFormLocation;
    private long _formVersion;
    private bool _formsLoading;
    private string? _formsError;
    private readonly Dictionary<PermissionId, SessionId> _tabPermissionOwners = [];
    public SessionFormSnapshot? Forms { get { lock (_gate) return _formSnapshot; } }

    public void ConfigureForms(SessionFormTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_gate) _formTransport = transport;
    }

    public async Task RefreshCurrentFormsAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);
        await RefreshFormsAsync(CurrentSession, cancellationToken);
    }

    private async Task RefreshFormsAsync(SessionInfo? session, CancellationToken cancellationToken)
    {
        var location = session?.Location ?? _create().Location
            ?? throw new InvalidOperationException("The current form location is unavailable.");
        SessionFormTransport? transport;
        lock (_gate)
        {
            _homeFormLocation = session is null ? location : _homeFormLocation ?? _create().Location ?? location;
            if (session is not null) _formSessions[session.Id] = (session.ParentId, session.Location);
            transport = _formTransport;
            _formsLoading = true;
            _formsError = null;
            PublishForms();
        }
        try
        {
            if (transport is null) throw new InvalidOperationException("The selected client has no form HTTP transport.");
            var owners = new List<(string Owner, LocationRef Location)>();
            if (session is not null && session.ParentId is null)
            {
                owners.Add((session.Id.Value, location));
                var parents = new Queue<SessionId>();
                var seen = new HashSet<SessionId> { session.Id };
                parents.Enqueue(session.Id);
                while (parents.TryDequeue(out var parent))
                {
                    string? cursor = null;
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    do
                    {
                        var page = await RequestAsync(token => _client.ListAsync(new SessionListQuery
                        {
                            ParentId = parent, Limit = 100, Cursor = cursor
                        }, token), "form session-family hydration", cancellationToken);
                        foreach (var child in page.Data)
                        {
                            if (!seen.Add(child.Id)) continue;
                            lock (_gate) _formSessions[child.Id] = (child.ParentId, child.Location);
                            owners.Add((child.Id.Value, child.Location));
                            parents.Enqueue(child.Id);
                        }
                        cursor = page.Cursor.Next;
                        if (cursor is not null && !cursors.Add(cursor)) throw new InvalidOperationException("The server repeated a session-family cursor.");
                    } while (cursor is not null);
                }
            }
            owners.Add(("global", location));
            foreach (var owner in owners)
            {
                if (owner.Owner != "global")
                    await RefreshFamilyPermissionAsync(OpenCode.Schema.SessionId.FromExisting(owner.Owner), cancellationToken);
                long version;
                lock (_gate) version = _formVersion;
                var forms = await RequestAsync(token => transport.List(owner.Owner, owner.Location, token), "pending form hydration", cancellationToken);
                if (forms.Any(form => form.SessionId != owner.Owner)) throw new InvalidOperationException("Form hydration returned a different owner.");
                lock (_gate)
                {
                    var ids = forms.Select(form => form.Id).ToHashSet();
                    foreach (var key in _forms.Keys.Where(key => key.Location == owner.Location && key.Owner == owner.Owner
                        && !ids.Contains(key.Id) && _formVersions.GetValueOrDefault(key) <= version).ToArray()) _forms.Remove(key);
                    foreach (var form in forms)
                    {
                        var key = (owner.Location, owner.Owner, form.Id);
                        if (_formVersions.GetValueOrDefault(key) <= version) _forms[key] = new(form, owner.Location);
                    }
                    PublishForms();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            lock (_gate) _formsError = $"Could not load pending forms: {Describe(exception)}";
        }
        finally
        {
            lock (_gate) { _formsLoading = false; PublishForms(); }
        }
    }

    public Task ReplyFormAsync(FormReplyRequest request, CancellationToken cancellationToken) => SettleFormAsync(
        request.SessionId, request.FormId, request.Location,
        transport => token => transport.Reply(request, token), "form reply", cancellationToken);

    public Task CancelFormAsync(FormCancelRequest request, CancellationToken cancellationToken) => SettleFormAsync(
        request.SessionId, request.FormId, request.Location,
        transport => token => transport.Cancel(request, token), "form cancellation", cancellationToken);

    private async Task SettleFormAsync(string owner, FormId id, LocationRef location,
        Func<SessionFormTransport, Func<CancellationToken, Task>> operation, string label, CancellationToken cancellationToken)
    {
        SessionFormTransport transport;
        lock (_gate)
        {
            transport = _formTransport ?? throw new InvalidOperationException("The selected client has no form HTTP transport.");
            if (!_forms.ContainsKey((location, owner, id))) throw new InvalidOperationException("The form is no longer pending. Refresh pending forms.");
        }
        try
        {
            await RequestAsync(async token => { await operation(transport)(token); return true; }, label, cancellationToken);
            lock (_gate) RemoveForm(location, owner, id);
        }
        catch (SessionApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            await RefreshCurrentFormsAsync(cancellationToken);
            throw;
        }
    }

    // Runs under the existing event-pump lock, before generic SessionId handling:
    // the canonical form owner may be "global", which is not a SessionId.
    private bool ApplyFormEvent(ServerEventEnvelope item)
    {
        if (item.Type == "permission.asked")
        {
            var permission = Decode(item, PermissionProtocolJsonContext.Default.PermissionRequest);
            _tabPermissionOwners[permission.Id] = permission.SessionId;
            UpdateTabAttention();
            return false;
        }
        if (item.Type == "permission.replied")
        {
            _tabPermissionOwners.Remove(PermissionId.FromExisting(item.Data.GetProperty("requestID").GetString()!));
            UpdateTabAttention();
            return false;
        }
        if (item.Type == "session.created")
        {
            var created = Decode(item, OpenCodeJsonContext.Default.SessionCreatedEventData);
            _formSessions[created.SessionId] = (created.ParentId, created.Location);
            PublishForms();
            return false;
        }
        if (item.Type is not ("form.created" or "form.replied" or "form.cancelled")) return false;
        if (item.Location is not { } location)
        {
            _formsError = "A form event is missing its location. Reload pending forms.";
            PublishForms();
            return true;
        }
        try
        {
            if (item.Type == "form.created")
            {
                var form = Decode(item, OpenCodeJsonContext.Default.FormCreatedEventData).Form;
                var key = (location, form.SessionId, form.Id);
                _formVersions[key] = ++_formVersion;
                _forms[key] = new(form, location);
                PublishForms();
            }
            if (item.Type == "form.replied")
            {
                var replied = Decode(item, OpenCodeJsonContext.Default.FormRepliedEventData);
                RemoveForm(location, replied.SessionId, replied.Id);
            }
            if (item.Type == "form.cancelled")
            {
                var cancelled = Decode(item, OpenCodeJsonContext.Default.FormCancelledEventData);
                RemoveForm(location, cancelled.SessionId, cancelled.Id);
            }
        }
        catch (JsonException exception) { _formsError = $"Could not read form event: {Describe(exception)}"; PublishForms(); }
        return true;
    }

    private void RemoveForm(LocationRef location, string owner, FormId id)
    {
        var key = (location, owner, id);
        _formVersions[key] = ++_formVersion;
        _forms.Remove(key);
        PublishForms();
    }

    private IReadOnlyList<SessionId> FormDescendants(SessionId root)
    {
        var result = new List<SessionId>();
        var parents = new Queue<SessionId>();
        var seen = new HashSet<SessionId> { root };
        parents.Enqueue(root);
        while (parents.TryDequeue(out var parent))
            foreach (var child in _formSessions.Where(pair => pair.Value.Parent == parent).Select(pair => pair.Key))
                if (seen.Add(child)) { result.Add(child); parents.Enqueue(child); }
        return result;
    }

    private bool IsPermissionInCurrentFamily(SessionId session) => _sessionId == session
        || CurrentSession is { ParentId: null } current && FormDescendants(current.Id).Contains(session);

    private async Task RefreshFamilyPermissionAsync(SessionId owner, CancellationToken cancellationToken)
    {
        long version;
        lock (_gate) version = _permissionVersion;
        var permissions = (await RequestAsync(token => _client.ListSessionPermissionsAsync(owner, token), "family permission hydration", cancellationToken)).Data;
        lock (_gate)
        {
            var ids = permissions.Select(permission => permission.Id).ToHashSet();
            foreach (var entry in _permissions.Where(pair => pair.Value.SessionId == owner && !ids.Contains(pair.Key)
                && _permissionVersions.GetValueOrDefault(pair.Key) <= version).ToArray()) _permissions.Remove(entry.Key);
            foreach (var permission in permissions)
                if (_permissionVersions.GetValueOrDefault(permission.Id) <= version)
                {
                    _permissions[permission.Id] = permission;
                    _tabPermissionOwners[permission.Id] = permission.SessionId;
                }
            _permissionSnapshot = _permissions.Values.ToArray();
        }
    }

    private bool PendingFormsBlockExecution => _pending is not null && _formSnapshot is { } snapshot
        && FormAdapter.FirstForRoute(snapshot.Pending, snapshot.Location, _pending.SessionId, FormDescendants(_pending.SessionId)) is not null;

    private void PublishForms()
    {
        UpdateTabAttention();
        var location = CurrentSession?.Location ?? _homeFormLocation;
        if (location is null) return;
        var descendants = CurrentSession is { ParentId: null } session ? FormDescendants(session.Id) : [];
        _formSnapshot = new(_sessionId, location, _forms.Values.ToImmutableArray(), descendants, _formsLoading, _formsError);
        if (_pending is not { } pending) return;
        // Wake the stream's wait so its idle deadline is removed while a form awaits input.
        pending.Status = PendingFormsBlockExecution ? "Form response required" : _permissions.Count > 0 ? "Permission required" : pending.Delivered ? "Running" : "Queued";
        pending.Publish();
    }

    private void UpdateTabAttention()
    {
        foreach (var id in _formSessions.Keys.Concat(_tabActivity.Keys).Distinct().ToArray())
        {
            var members = FormDescendants(id).Append(id).ToHashSet();
            var permission = _tabPermissionOwners.Values.Any(members.Contains);
            var question = _forms.Values.Any(form => members.Any(member => member.Value == form.Form.SessionId));
            SessionTabAttention? attention = permission ? SessionTabAttention.Permission : question ? SessionTabAttention.Question : null;
            var previous = _tabActivity.GetValueOrDefault(id) ?? new();
            if (previous.Attention != attention) _tabActivity = _tabActivity.SetItem(id, previous with { Attention = attention });
        }
    }
}
