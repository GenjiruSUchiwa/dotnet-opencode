using System.Diagnostics;
using System.Text;
using OpenCode.Cli.Tui;
using OpenCode.Client;
using OpenCode.Sdk;
using OpenCode.Server;

Console.OutputEncoding = Encoding.UTF8;

// Subcommand: serve
if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    var port = ServerHost.DefaultPort;
    for (int i = 1; i < args.Length; i++)
    {
        if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort))
        {
            port = parsedPort;
            break;
        }
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Starting opencode-dotnet daemon server on http://127.0.0.1:{port}...");
    Console.ResetColor();

    var app = ServerHost.CreateApp(args, port);
    await app.RunAsync();
    return;
}

// Subcommand: status / daemon management
if (args.Length > 0 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
{
    var discovered = await ServiceDaemon.DiscoverAsync();
    if (discovered is not null)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"opencode-dotnet daemon is RUNNING at {discovered.Url}");
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("opencode-dotnet daemon is NOT running.");
    }
    Console.ResetColor();
    return;
}

if (args.Length > 0 && args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Stopping opencode-dotnet daemon...");
    await ServiceDaemon.StopAsync();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("Daemon stopped.");
    Console.ResetColor();
    return;
}

// Subcommand: run <prompt> (Direct headless query)
if (args.Length > 0 && args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
{
    var runPrompt = string.Join(" ", args.Skip(1));
    if (string.IsNullOrWhiteSpace(runPrompt)) runPrompt = "Hello from opencode-dotnet!";

    await using var sdk = await OpenCodeClient.CreateAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

    await foreach (var chunk in sdk.AskAsync(runPrompt, ct: cts.Token))
    {
        Console.Write(chunk);
    }
    Console.WriteLine();
    return;
}

// Default / Subcommand: tui (Native C# interactive OpenCode TUI)
await InteractiveTui.RunAsync();
