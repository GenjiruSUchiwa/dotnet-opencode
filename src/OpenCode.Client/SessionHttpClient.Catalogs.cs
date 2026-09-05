namespace OpenCode.Client;

using OpenCode.Protocol;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public async Task<LocationResponse<IReadOnlyList<AgentInfo>>> ListAgentsAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/agent" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            CatalogProtocolJsonContext.Default.AgentsResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(item => item is null))
            throw Malformed("agent.list", "Agent catalog requires a non-null data array.");
        return result;
    }

    public async Task<LocationResponse<AgentInfo>> GetAgentAsync(AgentId agentId,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value, nameof(agentId));
        var result = await RequestAsync(HttpMethod.Get, "/api/agent/" + Uri.EscapeDataString(agentId.Value) + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            CatalogProtocolJsonContext.Default.AgentResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Id != agentId)
            throw Malformed("agent.get", "Agent response does not match the requested identity.");
        return result;
    }

    /// <summary>Returns server order and complete model metadata; does not filter disabled models or synthesize defaults.</summary>
    public async Task<LocationResponse<IReadOnlyList<ModelInfo>>> ListModelsAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/model" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            CatalogProtocolJsonContext.Default.ModelsResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(item => item is null))
            throw Malformed("model.list", "Model catalog requires a non-null data array.");
        return result;
    }

    public Task<DefaultModelResponse> DefaultModelAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/model/default" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            CatalogProtocolJsonContext.Default.DefaultModelResponse, ct);

    public async Task<LocationResponse<IReadOnlyList<ProviderInfo>>> ListProvidersAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/provider" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            CatalogProtocolJsonContext.Default.ProvidersResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(item => item is null))
            throw Malformed("provider.list", "Provider catalog requires a non-null data array.");
        return result;
    }
}
