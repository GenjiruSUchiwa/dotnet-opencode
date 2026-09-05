namespace OpenCode.Client;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task ActivateCredentialAsync(CredentialId credential, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, CredentialPath(credential) + "/activate" + IntegrationQuery(directory, workspace), ct);

    public Task UpdateCredentialAsync(CredentialId credential, string label, string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        return NoContentAsync(HttpMethod.Patch, CredentialPath(credential) + IntegrationQuery(directory, workspace), ct,
            JsonContent.Create(new CredentialLabelInput(label), CredentialHttpJsonContext.Default.CredentialLabelInput));
    }

    public Task RemoveCredentialAsync(CredentialId credential, string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Delete, CredentialPath(credential) + IntegrationQuery(directory, workspace), ct);

    /// <summary>Source has no credential GET route. Null means the integration is absent; an empty list means no saved connections.</summary>
    public async Task<IReadOnlyList<ConnectionCredentialInfo>?> ListCredentialConnectionsAsync(IntegrationId integration,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var result = await GetIntegrationAsync(integration, directory, workspace, ct).ConfigureAwait(false);
        return result.Data?.Connections.OfType<ConnectionCredentialInfo>().ToArray();
    }

    /// <summary>Sanitized ID/label projection through integration.get; never returns CredentialInfo.Value.</summary>
    public async Task<ConnectionCredentialInfo?> GetCredentialConnectionAsync(IntegrationId integration, CredentialId credential,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        (await ListCredentialConnectionsAsync(integration, directory, workspace, ct).ConfigureAwait(false))?.FirstOrDefault(item => item.Id == credential);

    private static string CredentialPath(CredentialId id)
    {
        ArgumentNullException.ThrowIfNull(id.Value);
        return "/api/credential/" + Uri.EscapeDataString(id.Value);
    }
}

internal sealed record CredentialLabelInput([property: JsonPropertyName("label")] string Label);

[JsonSerializable(typeof(CredentialLabelInput))]
internal partial class CredentialHttpJsonContext : JsonSerializerContext;
