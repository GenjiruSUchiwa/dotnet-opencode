namespace OpenCode.Server.Pty;

using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Services;
using OpenCode.Server.Endpoints;

/// <summary>All callbacks are supplied by real host/Location services; no synthetic Location fallback.</summary>
public sealed record PtyHostOptions(
    Func<string?, string?, CancellationToken, ValueTask<LocationInfo>> ResolveLocation,
    Func<LocationInfo, string> ResolveShell,
    Func<HttpRequest, bool> Authorized,
    Func<LocationInfo, CancellationToken, ValueTask> FlushPlugins,
    Func<string, string, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> Environment,
    IReadOnlyList<string>? Cors = null);

public sealed class PtyHostServices(PtyLocationMap locations, PtyTickets tickets, PtyRequestPolicy policy, PtyHostOptions options)
{
    public PtyRequestPolicy Policy => policy;

    public async ValueTask<PtyLocationScope> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Query["location[directory]"].Count > 1 || request.Query["location[workspace]"].Count > 1)
            throw new RequestArgumentException("Location query parameters must occur only once.", nameof(request));
        var reference = RequestLocation.Reference(request);
        var location = await options.ResolveLocation(reference.Directory, reference.WorkspaceId?.Value, ct);
        return await locations.GetAsync(location, ct);
    }

    public PtyIntegration Integration(PtyLocationScope scope) => new(scope.Pty, tickets, scope.Location.WorkspaceId,
        (context, _) => { policy.RequireAuthentication(context.Request); return ValueTask.CompletedTask; },
        policy.IsAllowedOrigin, ct => options.FlushPlugins(scope.Location, ct), options.Environment);
}

public static class PtyHostRegistration
{
    public static IServiceCollection AddLocalPty(this IServiceCollection services, PtyHostOptions options)
    {
        var policy = new PtyRequestPolicy(options.Authorized, options.Cors);
        services.AddSingleton(options);
        services.AddSingleton<PtyTickets>();
        services.AddSingleton(policy);
        services.AddCors(cors => cors.AddPolicy("opencode-pty", builder => builder
            .SetIsOriginAllowed(policy.IsAllowedCorsOrigin).AllowAnyHeader().AllowAnyMethod()
            .SetPreflightMaxAge(TimeSpan.FromSeconds(86_400))));
        services.AddSingleton(provider => new PtyLocationMap(options.ResolveShell,
            (location, runtime) => new PtyEventBridge(location, runtime, provider.GetRequiredService<IEventFeedService>(),
                provider.GetRequiredService<ILogger<PtyEventBridge>>())));
        services.AddSingleton<PtyHostServices>();
        return services;
    }
}
