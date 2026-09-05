namespace OpenCode.Cli.Tui;

using OpenCode.Client;
using OpenCode.Schema;

/// <summary>Invalidation from committed notifications, not a second credential/MCP data store.</summary>
public readonly record struct ManagementRevision(long Global, long Location);

public sealed partial class SessionClientAdapter
{
    private long _managementGlobalRevision;
    private readonly Dictionary<LocationRef, long> _managementLocationRevisions = [];

    public ManagementRevision ReadManagementRevision(LocationRef location)
    {
        lock (_gate) return new(_managementGlobalRevision, _managementLocationRevisions.GetValueOrDefault(location));
    }

    // Receiver owner: call this for each envelope in the existing PumpAsync,
    // before Session-ID filtering. Credential notifications can be global.
    private void ObserveManagementEvent(ServerEventEnvelope item)
    {
        ObserveActivityEvent(item); // Same receiver; activity events are not consumed or resubscribed.
        if (item.Type is not ("credential.updated" or "credential.switched" or "integration.updated" or "mcp.status.changed")) return;
        lock (_gate)
        {
            if (item.Location is not { } location) { _managementGlobalRevision++; return; }
            _managementLocationRevisions[location] = _managementLocationRevisions.GetValueOrDefault(location) + 1;
        }
    }
}
