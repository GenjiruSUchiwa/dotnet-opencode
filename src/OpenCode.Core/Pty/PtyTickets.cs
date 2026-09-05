namespace OpenCode.Core.Pty;

using OpenCode.Schema;

public sealed record PtyTicketScope(PtyId PtyId, string? Directory = null, WorkspaceId? WorkspaceId = null);

/// <summary>Process-global, one-use tickets; authorization and origin checks belong to Server.</summary>
public sealed class PtyTickets(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private readonly Dictionary<string, (PtyTicketScope Scope, DateTimeOffset Expires)> tickets = [];

    public PtyConnectToken Issue(PtyTicketScope scope)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            foreach (var key in tickets.Where(x => x.Value.Expires <= now).Select(x => x.Key).ToArray()) tickets.Remove(key);
            if (tickets.Count >= 10_000) tickets.Remove(tickets.MinBy(x => x.Value.Expires).Key);
            var ticket = Guid.NewGuid().ToString();
            tickets.Add(ticket, (scope, now.AddSeconds(60)));
            return new(ticket, 60);
        }
    }

    public bool Consume(string ticket, PtyTicketScope scope)
    {
        lock (gate)
        {
            if (!tickets.TryGetValue(ticket, out var stored)) return false;
            if (stored.Expires <= time.GetUtcNow()) { tickets.Remove(ticket); return false; }
            if (stored.Scope != scope) return false;
            return tickets.Remove(ticket);
        }
    }
}
