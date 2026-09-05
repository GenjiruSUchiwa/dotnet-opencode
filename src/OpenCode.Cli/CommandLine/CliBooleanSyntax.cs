namespace OpenCode.Cli.CommandLine;

using System.CommandLine;

/// <summary>Compatibility shim for source boolean and short-flag lexical forms.
/// All symbols come from the tree; no actions or DTO binding live here.</summary>
internal static class CliBooleanSyntax
{
    internal static (string[] Tokens, IReadOnlyList<string> Errors) Normalize(RootCommand root, string[] args)
    {
        var output = new List<string>();
        var errors = new List<string>();
        var seen = new HashSet<Option>();
        var tokens = args.ToList();
        Command command = root;
        var inherited = new List<Option>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token == "--") { output.AddRange(tokens.Skip(index)); break; }
            var child = command.Subcommands.FirstOrDefault(candidate => candidate.Name.Equals(token, StringComparison.OrdinalIgnoreCase)
                || candidate.Aliases.Contains(token));
            if (child is not null)
            {
                inherited.AddRange(command.Options.Where(option => option.Recursive));
                command = child;
                output.Add(child.Name); // Preserve the former Program dispatch casing without duplicating commands/help.
                continue;
            }
            var equal = token.IndexOf('=');
            var name = equal < 0 ? token : token[..equal];
            var options = command.Options.Concat(inherited);
            var option = options.FirstOrDefault(value => value.Name == name || value.Aliases.Contains(name));
            if (option is null && token.Length > 2 && token[0] == '-' && token[1] != '-')
            {
                if (equal >= 0)
                {
                    // Source -ab=value is a single alias named ab, not a bundle.
                    errors.Add($"Unknown option: {name}");
                    output.Add(token);
                    continue;
                }
                // Source short clusters never treat the trailing letters as an
                // attached string value. Expand only lexical symbols, then let the
                // typed tree bind their values. Data/header/file values skip this.
                var cluster = token[1..].Select(character => "-" + character).ToArray();
                foreach (var flag in cluster[..^1])
                    if (options.FirstOrDefault(value => value.Name == flag || value.Aliases.Contains(flag)) is { } found
                        && found.ValueType != typeof(bool) && found.Arity.MinimumNumberOfValues > 0)
                        errors.Add($"Option {flag} needs a value separated by a space or '='.");
                tokens.RemoveAt(index);
                tokens.InsertRange(index, cluster);
                index--;
                continue;
            }
            var colon = name.IndexOf(':');
            if (option is null && equal < 0 && colon > 0 && options.Any(value => value.Name == name[..colon] || value.Aliases.Contains(name[..colon])))
            {
                // System.CommandLine also accepts ':'. Source does not. In
                // particular --auto:no must never become an implicit true flag.
                errors.Add("Use a space or '=' between an option and its value, not ':'.");
                output.Add(token);
                continue;
            }
            var negated = false;
            if (option is null && name.StartsWith("--no-", StringComparison.Ordinal))
            {
                option = options.FirstOrDefault(value => value.ValueType == typeof(bool) && value.Name == "--" + name[5..]);
                negated = option is not null;
            }
            if (option?.ValueType != typeof(bool) || option.Action is not null)
            {
                // Stable 2.0.11 discards an empty inline value in tokenization.
                // Preserve --data=, --title= and other literal empty strings.
                if (option is not null && equal >= 0 && equal == token.Length - 1 && option.Arity.MaximumNumberOfValues > 0)
                { output.Add(name); output.Add(""); }
                else output.Add(token);
                // The next token is the value of a non-boolean option: never edit
                // literal --data/header/param/file/URL contents as command syntax.
                if (option is not null && equal < 0 && option.Arity.MinimumNumberOfValues > 0 && index + 1 < tokens.Count && tokens[index + 1] != "--")
                    output.Add(tokens[++index]);
                continue;
            }
            var following = index + 1 < tokens.Count ? Boolean(tokens[index + 1]) : null;
            var first = seen.Add(option);
            if (negated)
            {
                if (equal >= 0 || following is not null) errors.Add($"Negated flag {name} cannot have a value.");
                if (equal < 0 && following is not null) index++;
                output.Add(option.Name + "=false");
                continue;
            }
            if (equal >= 0)
            {
                var value = Boolean(token[(equal + 1)..]);
                if (value is null && first) errors.Add($"Invalid boolean value for {name}.");
                output.Add(name + "=" + (value ?? "false"));
                continue;
            }
            output.Add(name + "=" + (following ?? "true"));
            if (following is not null) index++;
        }
        return (output.ToArray(), errors);
    }
    private static string? Boolean(string value) => value switch
    {
        "true" or "yes" or "on" or "1" or "y" => "true",
        "false" or "no" or "off" or "0" or "n" => "false",
        _ => null
    };
}
