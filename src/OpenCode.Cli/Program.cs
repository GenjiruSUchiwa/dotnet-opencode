using System.Text;
using OpenCode.Sdk;

Console.OutputEncoding = Encoding.UTF8;

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
