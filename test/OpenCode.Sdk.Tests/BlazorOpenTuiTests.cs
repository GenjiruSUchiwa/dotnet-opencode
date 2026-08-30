namespace OpenCode.Sdk.Tests;

using OpenTui.Blazor;

public class BlazorOpenTuiTests
{
    [Fact]
    public async Task CanHostBlazorComponentTreeInOpenTui()
    {
        var host = new OpenTuiHost();
        var componentId = await host.RunAppAsync();

        Assert.True(componentId >= 0, "Root component ID should be non-negative.");
        Assert.NotNull(host.Renderer.RootNode);
    }
}
