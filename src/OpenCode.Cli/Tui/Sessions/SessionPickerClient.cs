namespace OpenCode.Cli.Tui.Sessions;

using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

/// <summary>Typed HTTP callbacks for the mounted picker. The root owns discovery, current Location and navigation.</summary>
public sealed class SessionPickerClient(Func<CancellationToken, Task<SessionHttpClient>> client,
    Func<CancellationToken, Task<LocationInfo>> location)
{
    public async Task<SessionPickerPage> LoadAsync(SessionPickerQuery query, CancellationToken ct)
    {
        var current = await location(ct);
        var api = await client(ct);
        var page = await api.ListAsync(new SessionListQuery
        {
            Limit = query.Limit, Cursor = query.Cursor, Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            RootOnly = true, Order = SessionOrder.Descending,
            Directory = !query.AllProjects && current.Project.Id.Value == "global" ? current.Directory : null,
            Project = !query.AllProjects && current.Project.Id.Value != "global" ? current.Project.Id : null,
            Subpath = !query.AllProjects && current.Project.Id.Value != "global"
                ? Path.GetRelativePath(current.Project.Directory, current.Directory).Replace('\\', '/') is var relative && relative == "." ? "" : relative : null
        }, ct);
        return new(page.Data, page.Cursor.Next);
    }

    public async Task RenameAsync(SessionId id, string title, CancellationToken ct) => await (await client(ct)).RenameAsync(id, title, ct);
    public async Task DeleteAsync(SessionId id, CancellationToken ct) => await (await client(ct)).DeleteAsync(id, ct);
}
