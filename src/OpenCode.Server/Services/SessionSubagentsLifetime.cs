namespace OpenCode.Server.Services;

using OpenCode.Core.Session.Subagents;

/// <summary>After execution settles, join child jobs/notifications before Location permissions and forms close.</summary>
internal sealed class SessionSubagentsLifetime(SessionSubagents subagents) : IHostedService
{
    public Task StartAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) => subagents.DisposeAsync().AsTask();
}
