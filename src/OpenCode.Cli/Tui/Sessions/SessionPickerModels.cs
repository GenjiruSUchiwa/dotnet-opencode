namespace OpenCode.Cli.Tui.Sessions;

using System.Globalization;
using OpenCode.Schema;

/// <summary>The controller maps scope to the canonical session.list query for its Location.</summary>
public sealed record SessionPickerQuery(string Search, string? Cursor, bool AllProjects, int Limit = 50);

public sealed record SessionPickerPage(IReadOnlyList<SessionInfo> Sessions, string? NextCursor = null);

public sealed record SessionPickerRow(SessionInfo Session, string Title, string Directory, string Category, string Updated, bool Active)
{
    public static SessionPickerRow From(SessionInfo session, bool active, DateTimeOffset now)
    {
        var updated = session.Time.Updated.ToLocalTime();
        return new(session,
            session.Title ?? $"{(session.ParentId is null ? "New" : "Child")} session - {session.Time.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}",
            session.Location.Directory,
            updated.Date == now.ToLocalTime().Date ? "Today" : updated.ToString("ddd, dd MMM yyyy", CultureInfo.CurrentCulture),
            updated.ToString("HH:mm", CultureInfo.CurrentCulture), active);
    }
}
