namespace OpenCode.Core.Reference;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

/// <summary>ConfigReferencePlugin plus local Reference materialization; no cloning or target-existence requirement.</summary>
internal static class ReferenceSources
{
    internal static IReadOnlyList<ReferenceInfo> Observe(IEnumerable<(string? Path, JsonObject Info)> documents,
        string location, string home)
    {
        var references = new Dictionary<string, ReferenceInfo>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            if (document.Info["references"] is null) continue;
            if (document.Info["references"] is not JsonObject entries) throw new JsonException("References must be an object.");
            var directory = document.Path is null ? location : Path.GetDirectoryName(Path.GetFullPath(document.Path))!;
            foreach (var entry in entries)
            {
                if (entry.Key.Length == 0 || Regex.IsMatch(entry.Key, @"[/\s`,]")) continue;
                string path;
                string? description = null;
                bool? hidden = null;
                if (entry.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                {
                    // These are the upstream shorthand discriminator rules. Windows
                    // absolute local paths should use the explicit { path: ... } form.
                    if (!(text.StartsWith('.') || text.StartsWith('/') || text.StartsWith('~')))
                        throw new NotSupportedException("Git reference materialization requires the native repository/cache service.");
                    path = text;
                }
                else if (entry.Value is JsonObject value)
                {
                    if (!value.ContainsKey("path"))
                        throw new NotSupportedException("Git reference materialization requires the native repository/cache service.");
                    path = RequiredString(value["path"]);
                    if (value.ContainsKey("description")) description = RequiredString(value["description"]);
                    if (value.ContainsKey("hidden"))
                        hidden = value["hidden"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled)
                            ? enabled : throw new JsonException("Reference hidden must be a boolean.");
                }
                else throw new JsonException("Reference must be a string or a source object.");
                var resolved = path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) :
                    Path.IsPathRooted(path) ? path : Path.Combine(directory, path);
                var source = new ReferenceLocalSource(Path.GetFullPath(resolved), description, hidden);
                // Replacing an alias replaces its entire definition, retaining its insertion position.
                references[entry.Key] = new ReferenceInfo(entry.Key, source.Path, source, description, hidden);
            }
        }
        return references.Values.ToArray();
    }

    internal static InstructionSource Instructions(IReadOnlyList<ReferenceInfo> references, bool available = true)
    {
        // Upstream guidance filters by description, not by hidden.
        var summaries = references.Where(reference => reference.Description is not null)
            .OrderBy(reference => reference.Name, StringComparer.CurrentCulture)
            .Select(reference => new { name = reference.Name, path = reference.Path, description = reference.Description }).ToArray();
        return new InstructionSource("core/reference-guidance", !available ? InstructionAvailability.Unavailable :
            summaries.Length == 0 ? InstructionAvailability.Removed : InstructionAvailability.Available,
            JsonSerializer.SerializeToElement(summaries), Render, Update,
            _ => "Project reference guidance is no longer available. Do not use previously listed references.");
    }

    private static string Render(JsonElement value) => string.Join("\n", new[]
    {
        "Project references provide additional directories that can be accessed when relevant.", "<available_references>"
    }.Concat(Entries(value.EnumerateArray())).Append("</available_references>"));

    private static IEnumerable<string> Entries(IEnumerable<JsonElement> references) => references.SelectMany(reference =>
        new[] { "  <reference>", $"    <name>{reference.GetProperty("name").GetString()}</name>",
            $"    <path>{reference.GetProperty("path").GetString()}</path>" }
        .Concat(reference.TryGetProperty("description", out var description) ? [$"    <description>{description.GetString()}</description>"] : Array.Empty<string>())
        .Append("  </reference>"));

    private static string Update(JsonElement previous, JsonElement current)
    {
        var before = previous.EnumerateArray().ToDictionary(value => value.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var after = current.EnumerateArray().ToDictionary(value => value.GetProperty("name").GetString()!, StringComparer.Ordinal);
        var added = after.Where(value => !before.ContainsKey(value.Key)).Select(value => value.Value).ToArray();
        var removed = before.Keys.Where(key => !after.ContainsKey(key)).ToArray();
        if (after.Any(value => before.TryGetValue(value.Key, out var old) &&
            (old.GetProperty("path").GetString() != value.Value.GetProperty("path").GetString() || Description(old) != Description(value.Value))) ||
            added.Length == 0 && removed.Length == 0)
            return "The available project references have changed. This list supersedes the previous reference list.\n" + Render(current);
        return string.Join("\n", (added.Length == 0 ? Array.Empty<string>() :
            new[] { "New project references are available in addition to those previously listed:" }.Concat(Entries(added)))
            .Concat(removed.Length == 0 ? Array.Empty<string>() :
                [$"The following project references are no longer available and must not be used: {string.Join(", ", removed)}."]));
    }

    private static string? Description(JsonElement value) => value.TryGetProperty("description", out var description) ? description.GetString() : null;
    private static string RequiredString(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text : throw new JsonException("Reference field must be a string.");
}
