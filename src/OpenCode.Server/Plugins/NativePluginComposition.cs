namespace OpenCode.Server.Plugins;

using System.Text.Json;
using OpenCode.Core.Plugins;
using OpenCode.Schema;
using OpenCode.Server.Services;

internal static class NativePluginComposition
{
    internal static IReadOnlyList<NativePluginDefinition> Definitions(IServiceProvider services, LocationInfo location) =>
        services.GetServices<INativePluginSource>().SelectMany(source => source.Definitions(location)).ToArray();

    internal static void Publish(IServiceProvider services, LocationInfo location, PluginId? id)
    {
        var data = id is { } plugin
            ? JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["id"] = plugin.Value })
            : JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        services.GetRequiredService<IEventFeedService>().Publish(new OpenCodeEvent(EventId.Create(),
            id is null ? "plugin.updated" : "plugin.added", services.GetRequiredService<TimeProvider>().GetUtcNow().ToUnixTimeMilliseconds(), data,
            new LocationRef(location.Directory, location.WorkspaceId)));
    }
}
