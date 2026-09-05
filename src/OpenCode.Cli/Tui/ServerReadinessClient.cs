namespace OpenCode.Cli.Tui;

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Client;
using OpenCode.Protocol.Groups;

internal sealed record ServerExecutionCapabilities(
    [property: JsonPropertyName("canExecute"), JsonRequired] bool CanExecute,
    [property: JsonPropertyName("instructions"), JsonRequired] bool Instructions,
    [property: JsonPropertyName("tools"), JsonRequired] bool Tools,
    [property: JsonPropertyName("unavailableReason")] string? UnavailableReason = null);

/// <summary>Reads the native server's declared execution gate; health alone is not execution readiness.</summary>
internal sealed class ServerReadinessClient(ServiceEndpoint endpoint) : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    internal async Task<ServerExecutionCapabilities> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(endpoint.Url), "/api/session/capabilities"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        endpoint.ApplyAuth(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Server execution readiness is unavailable (HTTP {(int)response.StatusCode}). No prompt was admitted.");
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("Server execution readiness did not return JSON. No prompt was admitted.");
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (await JsonSerializer.DeserializeAsync(body, ServerReadinessJsonContext.Default.CapabilitiesResult, cancellationToken))?.Data
            ?? throw new JsonException("Server execution readiness requires a data envelope.");
    }

    public void Dispose() => _http.Dispose();
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApiResult<ServerExecutionCapabilities>), TypeInfoPropertyName = "CapabilitiesResult")]
internal partial class ServerReadinessJsonContext : JsonSerializerContext;
