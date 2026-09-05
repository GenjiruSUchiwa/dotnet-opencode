namespace OpenCode.Cli.CommandLine;

using System.Globalization;
using Microsoft.Extensions.Hosting;
using OpenCode.Cli.Hosting;
using OpenCode.Cli.Tui;
using OpenCode.Client;
using OpenCode.Server;

public sealed record TuiOptions(string? Server, bool Standalone);
public sealed record ServeOptions(int? Port, string? RegistrationFile, string? ServiceConfig,
    string? StartupId, string? StartupReport, bool Service);

internal static class LifecycleCommands
{
    internal static async Task<int> TuiAsync(TuiOptions input, CancellationToken ct, TimeProvider clock)
    {
        try
        {
            var server = input.Server is null ? null : new ServiceEndpoint(new Uri(input.Server).GetLeftPart(UriPartial.Authority),
                Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVER_PASSWORD"));
#pragma warning disable MA0004 // Nullable private-host lease disposal retains its original captured-context semantics around the native TUI.
            await using var standalone = input.Standalone ? await StandaloneHostLease.StartAsync(ct, clock).ConfigureAwait(false) : null;
#pragma warning restore MA0004
            await InteractiveTui.RunAsync(standalone?.Endpoint ?? server, clock).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"Unable to run the native TUI: {error.Message}"); return 1; }
    }
    internal static async Task<int> ServeAsync(ServeOptions input, CancellationToken ct, TimeProvider clock)
    {
        // ServerHost's existing public boundary accepts its own argument format.
        // These tokens are an adapter from validated typed values, never original argv.
        var serverArguments = new List<string> { "serve" };
        foreach (var pair in new[]
        {
            ("--port", input.Port?.ToString(CultureInfo.InvariantCulture)), ("--registration-file", input.RegistrationFile),
            ("--service-config", input.ServiceConfig), ("--startup-id", input.StartupId), ("--startup-report", input.StartupReport)
        })
            if (pair.Item2 is not null) { serverArguments.Add(pair.Item1); serverArguments.Add(pair.Item2); }
        if (input.Service) serverArguments.Add("--service");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Starting dotnet opencode daemon server on http://127.0.0.1:{input.Port ?? ServerHost.DefaultPort}...");
        Console.ResetColor();
        var app = ServerHost.CreateApp(serverArguments.ToArray(), input.Port ?? ServerHost.DefaultPort, clock: clock);
        await using var appLifetime = app.ConfigureAwait(true);
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    }
    internal static async Task<int> StatusAsync(CancellationToken ct, TimeProvider clock)
    {
        var endpoint = await ServiceDaemon.DiscoverAsync(ct: ct, clock: clock).ConfigureAwait(false);
        Console.ForegroundColor = endpoint is null ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.WriteLine(endpoint is null ? "dotnet opencode daemon is NOT running." : $"dotnet opencode daemon is RUNNING at {endpoint.Url}");
        Console.ResetColor();
        return 0;
    }
    internal static async Task<int> StopAsync(CancellationToken ct, TimeProvider clock)
    {
        Console.WriteLine("Stopping dotnet opencode daemon...");
        await ServiceDaemon.StopAsync(ct: ct, clock: clock).ConfigureAwait(false);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Daemon stopped.");
        Console.ResetColor();
        return 0;
    }
}
