using System.Diagnostics;
using System.Text;
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

// Subcommand: tui (Ensures 1 and only 1 daemon, launches TUI on separate port 5055)
if (args.Length > 0 && args[0].Equals("tui", StringComparison.OrdinalIgnoreCase))
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Ensuring opencode-dotnet background daemon is running...");
    Console.ResetColor();

    var endpoint = await ServiceDaemon.EnsureAsync(port: ServerHost.DefaultPort);
    Console.WriteLine($"Connected to daemon at {endpoint.Url}");

    // Launch OpenCode TUI connected to this isolated port
    var tuiProcess = Process.Start(new ProcessStartInfo
    {
        FileName = "opencode2",
        ArgumentList = { "--server", endpoint.Url },
        UseShellExecute = false
    });

    if (tuiProcess is not null)
    {
        await tuiProcess.WaitForExitAsync();
    }
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

// Default command: streaming prompt execution via the engine
var prompt = args.Length > 0
    ? string.Join(" ", args)
    : "Introduce yourself, explain what model you are, and summarize quantum entanglement in two exciting sentences!";

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("================================================================================");
Console.WriteLine(" opencode-dotnet | Running model: gemini-flash (variant: high)");
Console.WriteLine("================================================================================");
Console.ResetColor();
Console.WriteLine($"Prompt: \"{prompt}\"\n");

await using var client = await OpenCodeClient.CreateAsync();
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

Console.ForegroundColor = ConsoleColor.Green;
await foreach (var chunk in client.AskAsync(prompt, modelId: "gemini-flash", variant: "high", ct: cts.Token))
{
    Console.Write(chunk);
}
Console.ResetColor();
Console.WriteLine("\n");
