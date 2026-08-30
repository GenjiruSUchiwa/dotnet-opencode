namespace OpenCode.Cli.Tui;

using System.Text;
using OpenCode.Client;
using OpenCode.Sdk;
using OpenCode.Server;

public sealed class InteractiveTui
{
    private static readonly string[] LogoLeft =
    [
        "                   ",
        "█▀▀█ █▀▀█ █▀▀█ █▀▀▄",
        "█__█ █__█ █^^^ █__█",
        "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀~~▀"
    ];

    private static readonly string[] LogoRight =
    [
        "             ▄     ",
        "█▀▀▀ █▀▀█ █▀▀█ █▀▀█",
        "█___ █__█ █__█ █^^^",
        "▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀"
    ];

    public static async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // Enter alternate terminal buffer
        Console.Write("\x1b[?1049h\x1b[?25l");

        try
        {
            await ServiceDaemon.EnsureAsync(port: ServerHost.DefaultPort);
            await using var client = await OpenCodeClient.CreateAsync();

            var currentInput = new StringBuilder();
            var history = new List<(string Prompt, string Response)>();
            var cwd = Directory.GetCurrentDirectory();

            while (true)
            {
                int width = Math.Max(80, Console.WindowWidth);
                int height = Math.Max(24, Console.WindowHeight);

                var canvas = new TerminalCanvas(width, height);
                RenderScreen(canvas, currentInput.ToString(), history, cwd);
                canvas.Flush();

                // Position cursor
                if (history.Count == 0)
                {
                    int cardX = Math.Max(2, (width - 75) / 2);
                    int promptRow = 14;
                    Console.Write($"\x1b[{promptRow + 1};{cardX + 4 + currentInput.Length}H\x1b[?25h");
                }

                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C))
                {
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (currentInput.Length > 0)
                    {
                        currentInput.Remove(currentInput.Length - 1, 1);
                    }
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    var promptText = currentInput.ToString().Trim();
                    if (string.IsNullOrEmpty(promptText)) continue;

                    currentInput.Clear();

                    // Switch to inline streaming view
                    Console.Write("\x1b[?25h\x1b[0m\n\n");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"You: {promptText}\n");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("Assistant: ");

                    var responseBuilder = new StringBuilder();
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

                    try
                    {
                        await foreach (var chunk in client.AskAsync(promptText, ct: cts.Token))
                        {
                            Console.Write(chunk);
                            responseBuilder.Append(chunk);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"\n[Error: {ex.Message}]");
                    }

                    Console.ResetColor();
                    Console.WriteLine("\n\nPress any key to return to prompt box...");
                    Console.ReadKey(intercept: true);

                    history.Add((promptText, responseBuilder.ToString()));
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    currentInput.Append(key.KeyChar);
                }
            }
        }
        finally
        {
            // Exit alternate buffer and show cursor
            Console.Write("\x1b[?25h\x1b[?1049l\x1b[0m");
        }
    }

    private static void RenderScreen(
        TerminalCanvas canvas,
        string currentInput,
        List<(string Prompt, string Response)> history,
        string cwd)
    {
        canvas.Clear(RgbColor.Black);

        // 1. Draw Wordmark
        int logoWidth = 40;
        int logoX = Math.Max(2, (canvas.Width - logoWidth) / 2);
        int logoY = 6;

        // Subtle "dotnet" above the wordmark in official .NET blurple (#7B61FF)
        string dotnetHeader = "d  o  t  n  e  t";
        int dotnetX = logoX + (logoWidth - dotnetHeader.Length) / 2;
        canvas.DrawString(dotnetX, logoY - 1, dotnetHeader, RgbColor.DotnetBlurple, bold: true);

        // Draw the dual-tone OpenCode wordmark lines
        for (int row = 0; row < 4; row++)
        {
            DrawLogoLine(canvas, logoX, logoY + row, LogoLeft[row], RgbColor.Gray, RgbColor.Charcoal, bold: false);
            DrawLogoLine(canvas, logoX + 20, logoY + row, LogoRight[row], RgbColor.White, RgbColor.DarkGray, bold: true);
        }

        // 2. Draw Center Prompt Input Card
        int cardWidth = Math.Min(75, canvas.Width - 4);
        int cardX = Math.Max(2, (canvas.Width - cardWidth) / 2);
        int cardY = 13;
        int cardHeight = 4;

        // Elevated card background
        canvas.FillRect(cardX + 1, cardY, cardWidth - 1, cardHeight, RgbColor.CardBg);

        // Left accent blue bar
        for (int y = cardY; y < cardY + cardHeight; y++)
        {
            canvas.DrawChar(cardX, y, '┃', RgbColor.AccentBlue, RgbColor.Black, bold: true);
        }
        canvas.DrawChar(cardX, cardY + cardHeight, '╹', RgbColor.AccentBlue, RgbColor.Black, bold: true);

        // Bottom shadow line
        for (int x = cardX + 1; x < cardX + cardWidth; x++)
        {
            canvas.DrawChar(x, cardY + cardHeight, '▀', RgbColor.CardBg, RgbColor.Black);
        }

        // Prompt input line
        string displayText = string.IsNullOrEmpty(currentInput)
            ? "Ask anything… \"Fix a TODO in the codebase\""
            : currentInput;
        var displayColor = string.IsNullOrEmpty(currentInput) ? RgbColor.Gray : RgbColor.PureWhite;
        canvas.DrawString(cardX + 3, cardY + 1, displayText, displayColor, RgbColor.CardBg);

        // Badge line inside card
        canvas.DrawString(cardX + 3, cardY + 2, "Build", RgbColor.AccentBlue, RgbColor.CardBg);
        canvas.DrawString(cardX + 9, cardY + 2, "·", RgbColor.Gray, RgbColor.CardBg);
        canvas.DrawString(cardX + 11, cardY + 2, "Gemini 3.7 Flash", RgbColor.Gray, RgbColor.CardBg);

        // 3. Under-card subtitle metadata
        int metaY = cardY + cardHeight + 1;
        var truncCwd = TruncatePath(cwd, 35);
        canvas.DrawString(cardX, metaY, truncCwd, RgbColor.Gray);

        int keybindX = cardX + cardWidth - 28;
        canvas.DrawString(keybindX, metaY, "shift+tab", RgbColor.White, bold: true);
        canvas.DrawString(keybindX + 10, metaY, "agents", RgbColor.Gray);
        canvas.DrawString(keybindX + 18, metaY, "ctrl+p", RgbColor.White, bold: true);
        canvas.DrawString(keybindX + 25, metaY, "commands", RgbColor.Gray);

        // 4. Bottom Footer Status Bar (1 row at bottom)
        int footerY = canvas.Height - 1;
        canvas.FillRect(0, footerY, canvas.Width, 1, RgbColor.BottomBarBg);

        int fx = 1;
        canvas.DrawChar(fx, footerY, '✓', RgbColor.SuccessGreen, RgbColor.BottomBarBg);
        canvas.DrawString(fx + 2, footerY, "Server", RgbColor.Gray, RgbColor.BottomBarBg);

        fx += 11;
        canvas.DrawChar(fx, footerY, '○', RgbColor.Gray, RgbColor.BottomBarBg);
        canvas.DrawString(fx + 2, footerY, "UI", RgbColor.Gray, RgbColor.BottomBarBg);

        fx += 7;
        canvas.DrawString(fx, footerY, "Theme", RgbColor.Gray, RgbColor.BottomBarBg);

        fx += 8;
        canvas.DrawString(fx, footerY, "Tools", RgbColor.Gray, RgbColor.BottomBarBg);

        fx += 8;
        canvas.DrawString(fx, footerY, "Experiments", RgbColor.Gray, RgbColor.BottomBarBg);

        string version = "10.0.0-opencode-dotnet";
        canvas.DrawString(canvas.Width - version.Length - 2, footerY, version, RgbColor.Gray, RgbColor.BottomBarBg);
    }

    private static void DrawLogoLine(
        TerminalCanvas canvas,
        int startX,
        int y,
        string line,
        RgbColor fg,
        RgbColor shadow,
        bool bold)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '_')
            {
                canvas.DrawChar(startX + i, y, ' ', fg, bg: shadow, bold);
            }
            else if (c == '^')
            {
                canvas.DrawChar(startX + i, y, '▀', fg, bg: shadow, bold);
            }
            else if (c == '~')
            {
                canvas.DrawChar(startX + i, y, '▀', shadow, bold: bold);
            }
            else if (c == ',')
            {
                canvas.DrawChar(startX + i, y, '▄', shadow, bold: bold);
            }
            else
            {
                canvas.DrawChar(startX + i, y, c, fg, bold: bold);
            }
        }
    }

    private static string TruncatePath(string path, int max)
    {
        if (path.Length <= max) return path;
        return "..." + path[^(max - 3)..];
    }
}
