namespace OpenCode.Server.Pty;

using OpenCode.Core.Pty;
using OpenCode.Server.Services;

public static class PersistentPtyHost
{
    public static IServiceCollection AddPersistentPty(this IServiceCollection services, PersistentPtyOptions? options = null)
    {
        options ??= new PersistentPtyOptions(Path.Combine(
            Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PTY_RUNTIME_DIR") ?? Path.Combine(Path.GetTempPath(), "opencode-pty-dotnet"),
            Guid.NewGuid().ToString("N")),
            Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PTY_BIN") ?? Environment.GetEnvironmentVariable("OPENCODE_PTY_BIN"));
        services.AddSingleton(options);
        services.AddSingleton<PersistentPtyDaemon>();
        services.AddSingleton(provider => new PersistentPtyService(provider.GetRequiredService<PersistentPtyDaemon>(),
            PtyShellSelection.ResolveEnvironment, provider.GetRequiredService<IEventFeedService>().Publish));
        services.AddHostedService<PersistentPtyLifetime>();
        return services;
    }

    private sealed class PersistentPtyLifetime(PersistentPtyService service) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => service.InitializeAsync(ct);
        public Task StopAsync(CancellationToken ct) => service.DisposeAsync().AsTask();
    }
}
