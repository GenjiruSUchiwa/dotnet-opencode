namespace OpenCode.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task AddMcpServerAsync(string server, McpServerConfig config, string? directory = null,
        string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return NoContentAsync(HttpMethod.Put, McpPath(server, null, directory, workspace), ct,
            JsonContent.Create(new McpAddPayload(config), McpHttpJsonContext.Default.McpAddPayload));
    }

    public Task RemoveMcpServerAsync(string server, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, McpPath(server, null, directory, workspace), ct);

    public Task ConnectMcpServerAsync(string server, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, McpPath(server, "/connect", directory, workspace), ct);

    public Task DisconnectMcpServerAsync(string server, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, McpPath(server, "/disconnect", directory, workspace), ct);

    private static string McpPath(string server, string? operation, string? directory, string? workspace)
    {
        ArgumentNullException.ThrowIfNull(server);
        return "/api/mcp/" + Uri.EscapeDataString(server) + operation + Query(
            ("location[directory]", directory), ("location[workspace]", workspace));
    }

    public async Task<LocationResponse<IReadOnlyList<McpServer>>> ListMcpServersAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(HttpMethod.Get, "/api/mcp" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            McpHttpJsonContext.Default.McpServersResult, ct).ConfigureAwait(false);
        if (result.Data is null || result.Data.Any(server => server is null))
            throw Malformed("mcp.list", "MCP catalog requires a non-null data array.");
        return result;
    }

    public Task<LocationResponse<McpResourceCatalog>> McpResourceCatalogAsync(
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/mcp/resource" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)),
            McpHttpJsonContext.Default.McpResourcesResult, ct);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<McpServer>>), TypeInfoPropertyName = "McpServersResult")]
[JsonSerializable(typeof(LocationResponse<McpResourceCatalog>), TypeInfoPropertyName = "McpResourcesResult")]
[JsonSerializable(typeof(McpAddPayload))]
internal partial class McpHttpJsonContext : JsonSerializerContext;
