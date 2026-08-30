namespace OpenCode.Cli.Tui;

public static class Wordmark
{
    // .NET brand blurple color: RGB(123, 97, 255) / #7B61FF
    private const string Blurple = "\x1b[38;2;123;97;255m";
    private const string Subdued = "\x1b[38;2;128;128;128m";
    private const string DefaultFg = "\x1b[38;2;238;238;238m";
    private const string Bold = "\x1b[1m";
    private const string Reset = "\x1b[0m";

    public static void Render(int consoleWidth = 80)
    {
        // 1. Subtle "dotnet" above the wordmark in .NET blurple
        string dotnetHeader = $"{Blurple}{Bold}d  o  t  n  e  t{Reset}";

        // 2. OpenCode wordmark lines from packages/tui/src/logo.ts
        var leftLines = new[]
        {
            "█▀▀█ █▀▀█ █▀▀█ █▀▀▄",
            "█  █ █  █ █▀▀▀ █  █",
            "▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀  ▀"
        };

        var rightLines = new[]
        {
            "█▀▀▀ █▀▀█ █▀▀█ █▀▀█",
            "█    █  █ █  █ █▀▀▀",
            "▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀"
        };

        int logoWidth = 40;
        int leftPad = Math.Max(2, (consoleWidth - logoWidth) / 2);
        string pad = new(' ', leftPad);

        // Print the subtle dotnet line centered above opencode
        int dotnetPad = Math.Max(2, leftPad + (logoWidth - 15) / 2);
        Console.WriteLine(new string(' ', dotnetPad) + dotnetHeader + "\n");

        for (int i = 0; i < 3; i++)
        {
            var left = leftLines[i];
            var right = rightLines[i];

            // Render left in subdued, right in default bold
            Console.WriteLine($"{pad}{Subdued}{left} {DefaultFg}{Bold}{right}{Reset}");
        }
    }
}
