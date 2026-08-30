namespace OpenCode.Sdk.Tests;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenCode.Protocol.Groups;

public class ServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ServerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthyStatus()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var health = JsonSerializer.Deserialize<ServiceHealthResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(health);
        Assert.True(health.Healthy);
        Assert.True(health.Pid > 0);
    }

    [Fact]
    public async Task ServerEndpoint_ReturnsServerUrls()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/server");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var info = JsonSerializer.Deserialize<ServerInfoResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(info);
        Assert.NotEmpty(info.Urls);
    }

    [Fact]
    public async Task SessionsEndpoint_ReturnsSessionList()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/session?limit=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task EventStream_ReturnsSseAndConnectedFrame()
    {
        var client = _factory.CreateClient();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/event");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var line = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(line);
        Assert.StartsWith("data: ", line);
        Assert.Contains("server.connected", line);
    }

    [Fact]
    public async Task ProviderEndpoint_ReturnsProviderList()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/provider");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("data", out var dataProp));
        Assert.Equal(JsonValueKind.Array, dataProp.ValueKind);
    }
}
