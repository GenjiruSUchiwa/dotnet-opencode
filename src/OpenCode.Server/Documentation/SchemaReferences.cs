namespace OpenCode.Server.Documentation;

using System.Text.Json.Nodes;

internal static class SchemaReferences
{
    internal static IReadOnlyList<string> Check(JsonObject schemas, JsonNode root)
    {
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Reference(string reference)
        {
            if (!reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
            { errors.Add("Unsupported schema reference: " + reference); return; }
            if (!seen.Add(reference)) return;
            JsonNode? node = schemas;
            foreach (var encoded in reference["#/components/schemas/".Length..].Split('/'))
            {
                var part = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var next)) { node = next; continue; }
                if (node is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count) { node = array[index]; continue; }
                errors.Add("Unresolved schema reference: " + reference); return;
            }
            Visit(node);
        }
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out var reference)) Reference(reference);
                if (obj["discriminator"] is JsonObject discriminator && discriminator["mapping"] is JsonObject mapping)
                    foreach (var pair in mapping) if (pair.Value is JsonValue target && target.TryGetValue<string>(out var path)) Reference(path);
                foreach (var child in obj) Visit(child.Value);
            }
            if (node is JsonArray array) foreach (var child in array) Visit(child);
        }
        Visit(root);
        return errors.Order(StringComparer.Ordinal).ToArray();
    }
}
