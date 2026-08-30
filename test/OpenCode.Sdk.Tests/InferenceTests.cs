namespace OpenCode.Sdk.Tests;

using System.Text;

public class InferenceTests
{
    [Fact]
    public async Task CanResolveActiveProviderFromConfig()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        var resolved = await client.Providers.ResolveAsync("google/gemini-2.5-flash");
        Assert.NotNull(resolved.Client);
        Assert.Contains("gemini-2.5-flash", resolved.ModelId);
    }

    [Fact]
    public async Task CanPerformLiveStreamingInference()
    {
        await using var client = await OpenCodeClient.CreateAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var output = new StringBuilder();
        await foreach (var chunk in client.AskAsync("Say 'GEMINI_FLASH_OK' and explain quantum entanglement in one sentence.", modelId: "google/gemini-2.5-flash", ct: cts.Token))
        {
            output.Append(chunk);
        }

        var result = output.ToString();
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("GEMINI_FLASH_OK", result, StringComparison.OrdinalIgnoreCase);
    }
}
