namespace OpenCode.Cli.CommandLine;

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;

/// <summary>Stable System.CommandLine keeps RootCommand.Name tied to the executable
/// and its HelpBuilder internal. Change only its usage prefix, not parser identity.</summary>
internal sealed class ToolHelpAction(SynchronousCommandLineAction original, string executableName) : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public override int Invoke(ParseResult result)
    {
        var output = result.InvocationConfiguration.Output;
        using var capture = new StringWriter(CultureInfo.InvariantCulture);
        int code;
        try
        {
            result.InvocationConfiguration.Output = capture;
            code = original.Invoke(result);
        }
        finally { result.InvocationConfiguration.Output = output; }
        using var lines = new StringReader(capture.ToString());
        while (lines.ReadLine() is { } line)
        {
            var start = line.Length - line.TrimStart().Length;
            var content = line.AsSpan(start);
            if (content.StartsWith(executableName, StringComparison.Ordinal)
                && (content.Length == executableName.Length || char.IsWhiteSpace(content[executableName.Length])))
                output.WriteLine(line[..start] + "dotnet opencode" + line[(start + executableName.Length)..]);
            else output.WriteLine(line);
        }
        return code;
    }
}
