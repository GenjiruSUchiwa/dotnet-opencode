namespace OpenCode.Sdk.Tests;

using System.Text;

public class InferenceTests
{
    [Fact]
    public async Task CanResolveActiveProviderFromConfig()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        var resolved = await client.Providers.ResolveAsync("gemini-flash", "high");
        Assert.NotNull(resolved.Client);
        Assert.Equal("gemini-flash", resolved.ModelId);
        Assert.NotNull(resolved.GenerationConfig);
    }

    [Fact]
    public async Task CanAskgemini-flashOnHighVariantFromConsoleLogin()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var output = new StringBuilder();
        await foreach (var chunk in client.AskAsync("Say 'gemini-flash_HIGH_OK' and explain quantum entanglement in one sentence.", modelId: "gemini-flash", variant: "high", ct: cts.Token))
        {
            output.Append(chunk);
        }

        var result = output.ToString();
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("gemini-flash_HIGH_OK", result, StringComparison.OrdinalIgnoreCase);
    }
}
