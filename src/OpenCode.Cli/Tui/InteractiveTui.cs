namespace OpenCode.Cli.Tui;

using System.Text;
using OpenCode.Client;
using OpenCode.Sdk;
using OpenCode.Server;

public sealed class InteractiveTui
{
    private const string Cyan = "\x1b[38;2;86;156;245m";
    private const string Dim = "\x1b[38;2;128;128;128m";
    private const string Green = "\x1b[38;2;127;216;143m";
    private const string White = "\x1b[38;2;238;238;238m";
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    public static async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = true;

        // 1. Ensure daemon is running
        Console.WriteLine("Connecting to opencode-dotnet daemon...");
        var endpoint = await ServiceDaemon.EnsureAsync(port: ServerHost.DefaultPort);

        await using var client = await OpenCodeClient.CreateAsync();

        var history = new List<(string Prompt, string Response)>();
        var activeModel = "Gemini 3.7 Flash";
        var activeAgent = "Build";
        var cwd = Directory.GetCurrentDirectory();

        while (true)
        {
            Console.Clear();
            int width = Math.Max(80, Console.WindowWidth);

            // 1. Render Wordmark with subtle blurple dotnet header
            Console.WriteLine("\n");
            Wordmark.Render(width);
            Console.WriteLine("\n");

            // 2. Render previous conversation turns if any
            if (history.Count > 0)
            {
                foreach (var turn in history)
                {
                    Console.WriteLine($"{Cyan}{Bold}User:{Reset} {turn.Prompt}\n");
                    Console.WriteLine($"{Green}{Bold}Assistant:{Reset} {turn.Response}\n");
                    Console.WriteLine($"{Dim}{new string('─', Math.Min(width - 4, 76))}{Reset}\n");
                }
            }

            // 3. Render Input Box
            int boxWidth = Math.Min(width - 8, 76);
            string line = new('━', boxWidth);

            Console.WriteLine($"   {Dim}┏{line}┓{Reset}");
            Console.WriteLine($"   {Dim}┃{Reset}  {White}Ask anything… \"Fix a bug in SessionStore\"{Reset}");
            Console.WriteLine($"   {Dim}┃{Reset}  {Cyan}{activeAgent}{Reset} {Dim}·{Reset} {White}{activeModel}{Reset}");
            Console.WriteLine($"   {Dim}┗{line}┛{Reset}");
            Console.WriteLine($"   {Dim}{TruncatePath(cwd, 40)}   {White}shift+tab{Reset} {Dim}agents{Reset}   {White}ctrl+p{Reset} {Dim}commands{Reset}\n");

            // 4. Render Footer Status Bar
            Console.WriteLine($"{Dim}✓ Server (port {ServerHost.DefaultPort})   ○ UI   Theme   Tools   Experiments            10.0.0-opencode-dotnet{Reset}\n");

            // 5. Prompt for input
            Console.Write($"   {Cyan}❯{Reset} ");
            var prompt = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(prompt))
            {
                continue;
            }

            if (prompt.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                prompt.Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // 6. Stream Assistant Response
            Console.WriteLine();
            Console.Write($"   {Green}{Bold}Assistant:{Reset} ");

            var responseBuilder = new StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            try
            {
                await foreach (var chunk in client.AskAsync(prompt, ct: cts.Token))
                {
                    Console.Write(chunk);
                    responseBuilder.Append(chunk);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Error streaming response: {ex.Message}]");
                Console.ResetColor();
            }

            Console.WriteLine("\n");
            history.Add((prompt, responseBuilder.ToString()));

            Console.WriteLine($"   {Dim}[Press any key to continue...]{Reset}");
            Console.ReadKey(intercept: true);
        }
    }

    private static string TruncatePath(string path, int max)
    {
        if (path.Length <= max) return path;
        return "..." + path[^(max - 3)..];
    }
}
