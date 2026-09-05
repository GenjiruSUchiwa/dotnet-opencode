namespace OpenCode.Core.CodeMode;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCode.Core.Tools;

public sealed record CodeModeCatalogEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonIgnore] bool Pinned = false);

public sealed record CodeModeSearchInput(string? Query = null, string? Namespace = null, int Limit = 10, int Offset = 0);
public sealed record CodeModeSearchPage([property: JsonPropertyName("offset")] int Offset);
public sealed record CodeModeSearchResult(
    [property: JsonPropertyName("items")] ImmutableArray<CodeModeCatalogEntry> Items,
    [property: JsonPropertyName("remaining")] int Remaining,
    [property: JsonPropertyName("next")] CodeModeSearchPage? Next);

/// <summary>Pure, captured discovery data. Search never refreshes registrations or grants authority.</summary>
public sealed class CodeModeCatalog
{
    private sealed record Indexed(CodeModeCatalogEntry Entry, string SearchText);
    private readonly ImmutableArray<Indexed> _index;
    public ImmutableArray<CodeModeCatalogEntry> Entries { get; }

    public const string SearchSignature = "search(input: { query?: string; namespace?: string; limit?: number; offset?: number }): { items: Array<{ path: string; description: string; signature: string }>; remaining: number; next: { offset: number } | null }";

    internal CodeModeCatalog(IEnumerable<ToolInfo> registrations)
    {
        // The registry already resolves effective-name collisions. Canonical paths use
        // namespace dots, not the flattened names advertised by direct tools.
        _index = registrations.GroupBy(QualifiedName, StringComparer.Ordinal).Select(group => group.Last())
            .OrderBy(QualifiedName, StringComparer.Ordinal).Select(tool =>
            {
                var path = QualifiedName(tool);
                var output = tool.Output is null ? "string | null" : CodeModeSignature.Render(tool.Output.JsonSchema);
                var entry = new CodeModeCatalogEntry(path, tool.Description,
                    $"{ToolExpression(path)}(input: {CodeModeSignature.Render(tool.Input.JsonSchema)}): Promise<{output}>", tool.Options?.Pinned == true);
                return new Indexed(entry, string.Join("\n", new[] { path, tool.Description }
                    .Concat(CodeModeSignature.InputProperties(tool.Input.JsonSchema))).ToLowerInvariant());
            }).ToImmutableArray();
        Entries = _index.Select(item => item.Entry).ToImmutableArray();
    }

    public CodeModeSearchResult Search(CodeModeSearchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Offset);
        var scoped = _index.Where(item => input.Namespace is null || item.Entry.Path == input.Namespace ||
            item.Entry.Path.StartsWith(input.Namespace + ".", StringComparison.Ordinal)).ToArray();
        var query = input.Query ?? "";
        var trimmed = query.Trim();
        var pathQuery = trimmed.StartsWith("tools.", StringComparison.Ordinal) ? trimmed[6..] : trimmed;
        var exact = pathQuery.Length == 0 ? null : scoped.FirstOrDefault(item =>
            item.Entry.Path == pathQuery || ToolExpression(item.Entry.Path) == trimmed);
        var terms = Regex.Split(Regex.Replace(query, "(?<lower>[a-z0-9])(?<upper>[A-Z])", "$1 $2", RegexOptions.NonBacktracking).ToLowerInvariant(), "[^a-z0-9]+", RegexOptions.NonBacktracking)
            .Where(term => term.Length > 0).Select(term => new[] { term }
                .Concat(term.EndsWith("es", StringComparison.Ordinal) && term.Length > 3 ? [term[..^2]] : [])
                .Concat(term.EndsWith('s') && term.Length > 2 ? [term[..^1]] : []).ToArray()).ToArray();
        var ranked = exact is not null ? new[] { exact } : scoped.Select(item =>
        {
            var path = item.Entry.Path.ToLowerInvariant();
            var description = item.Entry.Description.ToLowerInvariant();
            var score = terms.Sum(forms =>
                (forms.Any(form => path == form || path.EndsWith("." + form, StringComparison.Ordinal)) ? 20 : 0) +
                (forms.Any(form => path.Contains(form, StringComparison.Ordinal)) ? 8 : 0) +
                (forms.Any(form => description.Contains(form, StringComparison.Ordinal)) ? 4 : 0) +
                (forms.Any(form => item.SearchText.Contains(form, StringComparison.Ordinal)) ? 2 : 0));
            return (Item: item, Score: score);
        }).Where(item => terms.Length == 0 || item.Score > 0).OrderByDescending(item => item.Score)
            .ThenBy(item => item.Item.Entry.Path, StringComparer.Ordinal).Select(item => item.Item).ToArray();
        var items = ranked.Skip(input.Offset).Take(input.Limit)
            .Select(item => item.Entry with { Path = ToolExpression(item.Entry.Path) }).ToImmutableArray();
        var remaining = Math.Max(0, ranked.Length - input.Offset - items.Length);
        return new(items, remaining, remaining > 0 ? new(input.Offset + items.Length) : null);
    }

    public ImmutableArray<string> Keys(IReadOnlyList<string> path)
    {
        var prefix = string.Join(".", path);
        var children = Entries.Where(entry => prefix.Length == 0 || entry.Path.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Select(entry => entry.Path[(prefix.Length == 0 ? 0 : prefix.Length + 1)..].Split('.')[0])
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
        if (prefix.Length > 0 && children.Length == 0 && !Entries.Any(entry => entry.Path == prefix))
            throw new CodeModeDiagnosticException(new("UnknownTool", $"Unknown tool namespace '{prefix}'."));
        return children;
    }

    /// <summary>Pinned listings are always included; other listings share a round-robin token budget.</summary>
    public string RenderInstructions(int budget = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        if (Entries.IsEmpty) return "No Code Mode tools are currently available. Later Code Mode catalog updates may add or remove tools. Do not call `execute` unless there is at least one available Code Mode tool.";
        var groups = Entries.GroupBy(entry => entry.Path.Split('.')[0], StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Items: group.Select(entry =>
            {
                var description = entry.Description.Split('\n')[0].Trim();
                if (description.Length > 120) description = description[..117] + "...";
                var line = "  - " + entry.Signature + (description.Length == 0 ? "" : " // " + description);
                return (Entry: entry, Line: line, Cost: (int)Math.Floor(line.Length / 4.0 + 0.5));
            }).ToArray())).ToArray();
        var selected = Entries.Where(entry => entry.Pinned).Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        var remaining = budget - groups.SelectMany(group => group.Items).Where(item => item.Entry.Pinned).Sum(item => item.Cost);
        var queues = groups.Select(group => new Queue<(CodeModeCatalogEntry Entry, string Line, int Cost)>(group.Items
            .Where(item => !item.Entry.Pinned).OrderBy(item => item.Cost).ThenBy(item => item.Entry.Path, StringComparer.Ordinal))).ToArray();
        while (queues.Any(queue => queue.Count > 0))
            foreach (var queue in queues)
            {
                if (queue.Count == 0) continue;
                if (queue.Peek().Cost > remaining) { queue.Clear(); continue; }
                var item = queue.Dequeue();
                remaining -= item.Cost;
                selected.Add(item.Entry.Path);
            }
        var partial = selected.Count < Entries.Length;
        var header = $"The Code Mode tool catalog below is {(partial ? "partial" : "complete")}.\n\n" +
            (partial ? "The Code Mode catalog and `search` results are" : "This catalog is") +
            " the complete set of tools available within Code Mode. Tools presented elsewhere are not available in this runtime." +
            (partial ? "\n\n## Search\n\nUse `search` to discover exact paths and signatures for additional tools:\n\n- " + SearchSignature : "") + "\n\n## Available tools\n\n";
        return header + string.Join("\n", groups.SelectMany(group =>
        {
            var shown = group.Items.Where(item => selected.Contains(item.Entry.Path)).ToArray();
            var count = group.Items.Length + (group.Items.Length == 1 ? " tool" : " tools");
            var suffix = shown.Length == group.Items.Length ? "" : shown.Length == 0 ? ", none shown" : $", {shown.Length} shown";
            return new[] { $"- {group.Name} ({count}{suffix})" }.Concat(shown.Select(item => item.Line));
        }));
    }

    public static string ToolExpression(string path) => "tools" + string.Concat(path.Split('.').Select(segment =>
        CodeModeSignature.IsIdentifier(segment) ? "." + segment : "[" + JsonSerializer.Serialize(segment) + "]"));

    internal static string QualifiedName(ToolInfo tool) => tool.Options?.Namespace is { } space
        ? space + "." + ToolInfo.NormalizedName(tool.Name) : ToolInfo.NormalizedName(tool.Name);
}
