namespace OpenCode.Server.Services;

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Core.Permissions;
using OpenCode.Core.Projects;
using OpenCode.Core.Worktrees;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>One host-owned tool/permission graph, shared by HTTP and the runner.</summary>
public static class ToolHostComposition
{
    /// <summary>
    /// Call before registering the SessionExecutionService hosted service, so
    /// execution drains before the permission map closes during reverse-order shutdown.
    /// The options callback supplies resolved executables and real configured adapters.
    /// This composition alone does not advertise or execute tools.
    /// </summary>
    public static IServiceCollection AddLocalToolLocations(this IServiceCollection services, Func<LocationInfo, LocalToolOptions> local,
        Func<LocationInfo, IPermissionEvaluationHook?>? permissionHooks = null,
        Func<LocationInfo, ValueTask>? locationClosed = null)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (services.Any(service => service.ServiceType == typeof(ToolLocationFactory) ||
            service.ServiceType == typeof(PermissionLocationMap) || service.ServiceType == typeof(IPermissionLocationFactory) ||
            service.ServiceType == typeof(PermissionLocationServices) || service.ServiceType == typeof(IPermissionLocationServices)))
            throw new InvalidOperationException("Tool/permission Locations are already registered. HTTP and execution must share one factory and map.");

        // Explicit memory-only default. Core and HTTP refuse persistent always replies.
        // A host may register a real durable implementation before this composition.
        services.TryAddSingleton<IPermissionGrantStore, MemoryPermissionGrantStore>();
        services.AddSingleton(provider => new ToolLocationFactory(
            provider.GetRequiredService<SessionStore>(),
            async (location, ct) =>
            {
                if (location.WorkspaceId is not null)
                    throw new NotSupportedException("Native local tool Locations do not support workspace placement.");
                var info = await ProjectDiscovery.ResolveAsync(provider.GetRequiredService<IDatabase>(), location.Directory, ct: ct);
                // Project discovery may resolve physical paths. The service map's
                // authoritative key remains the stored lexical Location, not a new alias.
                return info with { Directory = location.Directory };
            },
            provider.GetRequiredService<IPermissionGrantStore>(),
            info =>
            {
                var options = local(info);
                if (!Path.IsPathFullyQualified(options.Home) || !Path.IsPathFullyQualified(options.RipgrepExecutable))
                    throw new NotSupportedException("The host must supply absolute home and ripgrep paths.");
                var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (!Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Home)).Equals(Path.TrimEndingDirectorySeparator(home),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new NotSupportedException("Tool home must match the host's AgentCatalog global home selection.");
                if (!File.Exists(options.RipgrepExecutable))
                    throw new NotSupportedException("The host's resolved ripgrep executable is unavailable.");
                var document = ConfigLoader.LoadDocument(directory: info.Directory);
                if (options.Formatter is null && document.TryGetPropertyValue("formatter", out var formatter) &&
                    !(formatter is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled))
                    throw new NotSupportedException("Configured formatting requires a native formatter adapter before local tools can be loaded.");
                return options;
            }, permissionHooks));
        services.AddSingleton<IPermissionLocationFactory>(provider => provider.GetRequiredService<ToolLocationFactory>());
        services.AddSingleton(provider =>
        {
            var feed = provider.GetRequiredService<IEventFeedService>();
            return new PermissionLocationMap(provider.GetRequiredService<ToolLocationFactory>(),
                async (info, permission) =>
                {
                    using var refreshStop = new CancellationTokenSource();
                    var refresh = RefreshWorktreesAsync(provider, info, refreshStop.Token);
                    try { await new PermissionEventBridge(info, permission, feed).RunAsync(default); }
                    finally
                    {
                        await refreshStop.CancelAsync();
                        await refresh;
                        if (locationClosed is not null) await locationClosed(info);
                    }
                });
        });
        services.AddSingleton<PermissionLocationServices>();
        services.AddSingleton<IPermissionLocationServices>(provider => provider.GetRequiredService<PermissionLocationServices>());
        services.AddHostedService<PermissionLocationServices>(provider => provider.GetRequiredService<PermissionLocationServices>());
        return services;
    }

    private static async Task RefreshWorktreesAsync(IServiceProvider services, LocationInfo location, CancellationToken ct)
    {
        if (services.GetService<WorktreeService>() is not { } worktrees) return;
        await Task.Yield();
        try { await worktrees.RefreshAsync(location.Project.Id, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(ToolHostComposition))
                .LogWarning(error, "Worktree refresh failed for {ProjectId}", location.Project.Id.Value);
        }
    }
}
