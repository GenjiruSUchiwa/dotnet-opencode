namespace OpenCode.Core.Instructions;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.CodeMode;

internal sealed record CodeModeListing([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("line")] string Line);
internal sealed record CodeModeNamespace([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("entries")] IReadOnlyList<CodeModeListing> Entries);
internal sealed record CodeModeSummary([property: JsonPropertyName("total")] int Total, [property: JsonPropertyName("shown")] int Shown,
    [property: JsonPropertyName("namespaces")] IReadOnlyList<CodeModeNamespace> Namespaces);

[JsonSerializable(typeof(CodeModeSummary))]
internal partial class CodeModeInstructionJsonContext : JsonSerializerContext;

/// <summary>codemode/catalog.ts summary and the retained core/codemode renderer.</summary>
internal static class CodeModeInstructionSource
{
    internal static InstructionSource Create(CodeModeCatalog? catalog) => new("core/codemode",
        catalog is null ? InstructionAvailability.Removed : InstructionAvailability.Available,
        catalog is null ? default : JsonSerializer.SerializeToElement(Summarize(catalog), CodeModeInstructionJsonContext.Default.CodeModeSummary),
        Render, (_, value) => "The Code Mode tool catalog has changed. This catalog supersedes the previous Code Mode tool catalog.\n\n" + Render(value),
        _ => "Code Mode tools are no longer available. Do not use any previously listed Code Mode tools.");

    private static CodeModeSummary Summarize(CodeModeCatalog catalog)
    {
        var groups = catalog.Entries.GroupBy(entry => entry.Path.Split('.')[0], StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Listings: group.OrderBy(entry => entry.Path, StringComparer.Ordinal).Select(entry =>
            {
                var line = entry.Description.Split('\n')[0].Trim();
                if (line.Length > 120) line = line[..117] + "...";
                var listing = new CodeModeListing(entry.Path, "  - " + entry.Signature + (line.Length == 0 ? "" : " // " + line));
                return (Listing: listing, entry.Pinned, Cost: (int)Math.Floor(listing.Line.Length / 4.0 + 0.5));
            }).ToArray())).ToArray();
        var selected = groups.SelectMany(group => group.Listings).Where(item => item.Pinned).Select(item => item.Listing.Path).ToHashSet(StringComparer.Ordinal);
        var remaining = 2000 - groups.SelectMany(group => group.Listings).Where(item => item.Pinned).Sum(item => item.Cost);
        var queues = groups.Select(group => new Queue<(CodeModeListing Listing, bool Pinned, int Cost)>(group.Listings.Where(item => !item.Pinned)
            .OrderBy(item => item.Cost).ThenBy(item => item.Listing.Path, StringComparer.Ordinal))).ToArray();
        while (queues.Any(queue => queue.Count > 0))
            foreach (var queue in queues)
            {
                if (queue.Count == 0) continue;
                if (queue.Peek().Cost > remaining) { queue.Clear(); continue; }
                var item = queue.Dequeue();
                selected.Add(item.Listing.Path);
                remaining -= item.Cost;
            }
        return new(catalog.Entries.Length, selected.Count, groups.Select(group => new CodeModeNamespace(group.Name, group.Listings.Length,
            group.Listings.Where(item => selected.Contains(item.Listing.Path)).Select(item => item.Listing).ToArray())).ToArray());
    }

    private static string Render(JsonElement value)
    {
        var summary = value.Deserialize(CodeModeInstructionJsonContext.Default.CodeModeSummary) ?? throw new JsonException("Stored Code Mode summary is unavailable.");
        if (summary.Total == 0) return "No Code Mode tools are currently available. Later Code Mode catalog updates may add or remove tools. Do not call `execute` unless there is at least one available Code Mode tool.";
        var partial = summary.Shown < summary.Total;
        var header = $"The Code Mode tool catalog below is {(partial ? "partial" : "complete")}.\n\n" +
            (partial ? "The Code Mode catalog and `search` results are" : "This catalog is") +
            " the complete set of tools available within Code Mode. Tools presented elsewhere are not available in this runtime." +
            (partial ? "\n\n## Search\n\nUse `search` to discover exact paths and signatures for additional tools:\n\n- " + CodeModeCatalog.SearchSignature : "") +
            "\n\n## Available tools\n\n";
        return header + string.Join('\n', summary.Namespaces.SelectMany(group =>
        {
            var count = group.Count == 1 ? "1 tool" : group.Count + " tools";
            var suffix = group.Entries.Count == group.Count ? "" : group.Entries.Count == 0 ? ", none shown" : $", {group.Entries.Count} shown";
            return new[] { $"- {group.Name} ({count}{suffix})" }.Concat(group.Entries.Select(entry => entry.Line));
        }));
    }
}
