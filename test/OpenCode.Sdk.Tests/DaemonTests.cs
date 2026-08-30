namespace OpenCode.Sdk.Tests;

using OpenCode.Client;

public class DaemonTests
{
    [Fact]
    public async Task ServiceDaemon_EnsuresSingleDaemonAndDiscoversIt()
    {
        var tempRegFile = Path.Combine(Path.GetTempPath(), $"opencode_test_service_{Guid.NewGuid():N}.json");

        try
        {
            // First ensure: Spawns or discovers the daemon
            var endpoint1 = await ServiceDaemon.EnsureAsync(tempRegFile, port: 5055);
            Assert.NotNull(endpoint1);
            Assert.NotEmpty(endpoint1.Url);

            // Discovery: Finds the exact same daemon
            var discovered = await ServiceDaemon.DiscoverAsync(tempRegFile);
            Assert.NotNull(discovered);
            Assert.Equal(endpoint1.Url, discovered.Url);

            // Second ensure: Must be idempotent and return the exact same endpoint without spawning another daemon
            var endpoint2 = await ServiceDaemon.EnsureAsync(tempRegFile, port: 5060);
            Assert.Equal(endpoint1.Url, endpoint2.Url);
        }
        finally
        {
            await ServiceDaemon.StopAsync(tempRegFile);
        }
    }
}
