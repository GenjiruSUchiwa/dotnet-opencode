namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task<IReadOnlyList<WorktreeDirectory>> ListWorktreesAsync(ProjectId projectId, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, WorktreePath(projectId), WorktreeProtocolJsonContext.Default.WorktreeList, ct);

    public Task<WorktreeInfo> CreateWorktreeAsync(WorktreeCreateInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequestAsync(HttpMethod.Post, WorktreePath(input.ProjectId), WorktreeProtocolJsonContext.Default.WorktreeInfo, ct,
            JsonContent.Create(new WorktreeCreatePayload(input.Strategy, input.Directory, input.From, input.Branch, input.Name),
                WorktreeProtocolJsonContext.Default.WorktreeCreatePayload));
    }

    public Task RemoveWorktreeAsync(WorktreeRemoveInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return NoContentAsync(HttpMethod.Delete, WorktreePath(input.ProjectId), ct,
            JsonContent.Create(new WorktreeRemovePayload(input.Directory, input.Force), WorktreeProtocolJsonContext.Default.WorktreeRemovePayload));
    }

    public Task RefreshWorktreesAsync(ProjectId projectId, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, WorktreePath(projectId) + "/refresh", ct);

    private static string WorktreePath(ProjectId id)
    {
        ArgumentNullException.ThrowIfNull(id.Value);
        return "/api/worktree/" + Uri.EscapeDataString(id.Value);
    }
}
