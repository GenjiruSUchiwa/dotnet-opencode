namespace OpenCode.Core.Commands;

using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using OpenCode.Schema;

/// <summary>Native opencode.command definitions. Tool use occurs only after normal Session admission.</summary>
public static class BuiltinCommands
{
    private static readonly ResourceManager Prompts = new("OpenCode.Core.Commands.BuiltinPrompts", typeof(BuiltinCommands).Assembly);

    public static IEnumerable<RuntimeCommand> Definitions(string projectDirectory)
    {
        yield return Create("init", "guided AGENTS.md setup", projectDirectory);
        yield return Create("review", "review changes [commit|branch|pr], defaults to uncommitted", projectDirectory);
    }

    private static RuntimeCommand Create(string name, string description, string projectDirectory)
    {
        var source = Prompts.GetString(name, CultureInfo.InvariantCulture)
            ?? throw new MissingManifestResourceException($"Missing built-in command prompt: {name}");
        // String.replace replaces only the first path token and interprets JS replacement tokens.
        var path = source.IndexOf("${path}", StringComparison.Ordinal);
        var template = path < 0 ? source : source[..path] + Regex.Replace(projectDirectory, @"\$(?<token>[$&`'])", token => token.Groups[1].Value switch
        {
            "$" => "$", "&" => "${path}", "`" => source[..path], "'" => source[(path + 7)..], _ => token.Value
        }, RegexOptions.NonBacktracking) + source[(path + 7)..];
        return new RuntimeCommand(new CommandInfo(name, description), (input, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            // ECMAScript trim excludes U+0085 and includes U+FEFF, unlike String.Trim().
            var value = CommandTemplate.Trim(input.Prompt.Text);
            var text = template.Contains("$ARGUMENTS", StringComparison.Ordinal)
                ? template.Replace("$ARGUMENTS", value, StringComparison.Ordinal)
                : string.Join("\n\n", new[] { template, value }.Where(part => part.Length > 0));
            return Task.FromResult(new PreparedCommand(name, input with { Prompt = input.Prompt with { Text = text } }));
        });
    }
}
