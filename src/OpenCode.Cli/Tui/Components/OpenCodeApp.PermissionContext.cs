namespace OpenCode.Cli.Tui.Components;

using OpenCode.Schema;

public partial class OpenCodeApp
{
    private (SessionId Session, PermissionId Request)? _permissionContextKey;
    private long _permissionContextRevision = -1;
    private string? _permissionContextError;

    private IReadOnlyList<SessionMessage> PermissionMessages => ActivePermission is { } permission
        ? ReadSessionObservation?.Invoke(permission.SessionId)?.Messages
            ?? (permission.SessionId == _sessionId ? _transcriptMessages : []) : [];

    private void ReadPermissionContext()
    {
        var permission = ActivePermission;
        var key = permission is null ? ((SessionId, PermissionId)?)null : (permission.SessionId, permission.Id);
        if (_permissionContextKey != key)
        {
            _permissionContextKey = key;
            _permissionContextError = null;
            _permissionContextRevision = -1;
            _dirty = true;
            if (permission is { Source.Type: "tool" } && PermissionTool is null)
                _keyTasks.Add(LoadPermissionContext(permission));
        }
        if (permission is null) return;
        var revision = ReadSessionObservation?.Invoke(permission.SessionId)?.Revision ?? -1;
        if (revision == _permissionContextRevision) return;
        _permissionContextRevision = revision;
        if (PermissionTool is not null) _permissionContextError = null;
        _dirty = true;
    }

    private async Task LoadPermissionContext(PermissionRequest permission)
    {
        try
        {
            if (ObserveActivitySession is null) throw new InvalidOperationException("Permission source observation is not connected.");
            var snapshot = await ObserveActivitySession(permission.SessionId, _configurationLifetime.Token);
            if (_permissionContextKey != (permission.SessionId, permission.Id)) return;
            if (snapshot.Error is not null) throw new InvalidOperationException(snapshot.Error);
            if (PermissionTool is null) _permissionContextError = "The requesting tool context is not available in the observed Session. The permission request itself is retained.";
        }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_permissionContextKey == (permission.SessionId, permission.Id))
                _permissionContextError = "Could not load permission source context: " + SessionClientAdapter.Describe(exception);
        }
        finally { _dirty = true; }
    }
}
