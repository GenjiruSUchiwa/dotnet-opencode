using System.Text;
using OpenCode.Sdk;
using OpenCode.Server;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
{
    var port = 5050;
    for (int i = 1; i < args.Length; i++)
    {
        if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedPort))
        {
            port = parsedPort;
            break;
        }
    }

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Starting opencode-dotnet server on http://127.0.0.1:{port}...");
    Console.ResetColor();

    var app = ServerHost.CreateApp(args, port);
    await app.RunAsync();
    return;
}

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
