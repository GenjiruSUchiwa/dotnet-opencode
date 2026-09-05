namespace OpenCode.Core.CodeMode;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>JSON-schema signatures, not executable code or a replacement for producer codecs.</summary>
internal static class CodeModeSignature
{
    internal static bool IsIdentifier(string value) => Regex.IsMatch(value, "^[A-Za-z_$][A-Za-z0-9_$]*$", RegexOptions.NonBacktracking);

    internal static string Render(JsonElement schema) => Render(schema, new(StringComparer.Ordinal), [], 0);

    private static string Render(JsonElement schema, Dictionary<string, JsonElement> definitions, HashSet<string> seen, int depth)
    {
        if (depth > 8 || schema.ValueKind != JsonValueKind.Object) return "unknown";
        var nested = Definitions(schema, definitions);
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = Reference(reference);
            if (name is null || seen.Contains(name) || !nested.TryGetValue(name, out var target)) return "unknown";
            return Intersection([Render(target, nested, new HashSet<string>(seen, StringComparer.Ordinal) { name }, depth),
                Render(Without(schema, "$ref"), nested, seen, depth + 1)]);
        }
        if (schema.TryGetProperty("const", out var constant)) return constant.GetRawText();
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array)
            return string.Join(" | ", values.EnumerateArray().Select(value => value.GetRawText()));
        if (schema.TryGetProperty("anyOf", out var alternatives) || schema.TryGetProperty("oneOf", out alternatives))
        {
            if (alternatives.ValueKind != JsonValueKind.Array) return "unknown";
            var members = alternatives.EnumerateArray().Select(item => Render(item, nested, seen, depth + 1)).ToArray();
            if (members.Contains("unknown")) return "unknown";
            return Intersection([string.Join(" | ", members), Render(Without(schema, "anyOf", "oneOf"), nested, seen, depth + 1)]);
        }
        if (schema.TryGetProperty("allOf", out var all))
        {
            if (all.ValueKind != JsonValueKind.Array || HasUnresolvedReference(all, nested, seen, depth)) return "unknown";
            return Intersection(all.EnumerateArray().Select(item => Render(item, nested, seen, depth + 1))
                .Prepend(Render(Without(schema, "allOf"), nested, seen, depth + 1)));
        }
        var type = schema.TryGetProperty("type", out var kind) ? kind : default;
        if (type.ValueKind == JsonValueKind.Array)
            return string.Join(" | ", type.EnumerateArray().Select(item => Render(JsonSerializer.SerializeToElement(
                schema.EnumerateObject().Where(property => property.Name != "type").ToDictionary(property => property.Name, property => property.Value)
                    .Append(new KeyValuePair<string, JsonElement>("type", item)).ToDictionary(pair => pair.Key, pair => pair.Value)), nested, seen, depth + 1)));
        var nameOfType = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        if (nameOfType is "string" or "boolean" or "null") return nameOfType;
        if (nameOfType is "number" or "integer") return "number";
        if (nameOfType == "array") return $"Array<{Render(schema.TryGetProperty("items", out var items) ? items : default, nested, seen, depth + 1)}>";
        if (nameOfType != "object" && !schema.TryGetProperty("properties", out _)) return "unknown";
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
        var properties = schema.TryGetProperty("properties", out var fields) && fields.ValueKind == JsonValueKind.Object
            ? fields.EnumerateObject().ToArray() : [];
        var pad = new string(' ', (depth + 1) * 2);
        var lines = properties.Select(property => Documentation(property.Value, pad) + pad +
            (IsIdentifier(property.Name) ? property.Name : JsonSerializer.Serialize(property.Name)) + (required.Contains(property.Name) ? "" : "?") +
            ": " + Render(property.Value, nested, seen, depth + 1) + ",").ToList();
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
            lines.Add(pad + "[key: string]: " + Render(additional, nested, seen, depth + 1) + ",");
        return lines.Count == 0 ? "{}" : "{\n" + string.Join("\n", lines) + "\n" + new string(' ', depth * 2) + "}";
    }

    internal static IEnumerable<string> InputProperties(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return [];
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = Reference(reference);
            if (name is null || !Definitions(schema, new(StringComparer.Ordinal)).TryGetValue(name, out var target)) return [];
            schema = target;
        }
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return [];
        return properties.EnumerateObject().SelectMany(property => new[] { property.Name }.Concat(
            property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
                ? [description.GetString()!] : [])).ToArray();
    }

    private static Dictionary<string, JsonElement> Definitions(JsonElement schema, Dictionary<string, JsonElement> previous)
    {
        var result = new Dictionary<string, JsonElement>(previous, StringComparer.Ordinal);
        foreach (var key in new[] { "definitions", "$defs" })
            if (schema.TryGetProperty(key, out var definitions) && definitions.ValueKind == JsonValueKind.Object)
                foreach (var property in definitions.EnumerateObject()) result[property.Name] = property.Value;
        return result;
    }

    private static string? Reference(JsonElement reference)
    {
        if (reference.ValueKind != JsonValueKind.String) return null;
        var match = Regex.Match(reference.GetString()!, "^#/(?:\\$defs|definitions)/(?<name>[^/]+)$", RegexOptions.NonBacktracking);
        return match.Success ? match.Groups[1].Value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal) : null;
    }

    private static bool HasUnresolvedReference(JsonElement value, Dictionary<string, JsonElement> definitions, HashSet<string> seen, int depth)
    {
        if (depth > 8) return true;
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(item => HasUnresolvedReference(item, definitions, seen, depth + 1));
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("$ref", out var reference))
        {
            var name = Reference(reference);
            if (name is null || !definitions.TryGetValue(name, out var target) || seen.Contains(name)) return true;
            if (HasUnresolvedReference(target, definitions, new HashSet<string>(seen, StringComparer.Ordinal) { name }, depth + 1)) return true;
        }
        return value.EnumerateObject().Where(property => property.Name is not ("$defs" or "definitions"))
            .Any(property => HasUnresolvedReference(property.Value, definitions, seen, depth + 1));
    }

    private static JsonElement Without(JsonElement schema, params string[] keys) => JsonSerializer.SerializeToElement(
        schema.EnumerateObject().Where(property => !keys.Contains(property.Name)).ToDictionary(property => property.Name, property => property.Value));

    private static string Intersection(IEnumerable<string> members)
    {
        var concrete = members.Where(member => member != "unknown").ToArray();
        return concrete.Length switch { 0 => "unknown", 1 => concrete[0], _ => string.Join(" & ", concrete.Select(member => member.Contains(" | ", StringComparison.Ordinal) ? "(" + member + ")" : member)) };
    }

    private static string Documentation(JsonElement schema, string pad)
    {
        if (schema.ValueKind != JsonValueKind.Object) return "";
        var lines = new List<string>();
        if (schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String) lines.AddRange(description.GetString()!.Split('\n'));
        if (schema.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True) lines.Add("@deprecated");
        foreach (var key in new[] { "default", "format", "minItems", "maxItems" })
            if (schema.TryGetProperty(key, out var value)) lines.Add("@" + key + " " + (key == "format" && value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()));
        var text = string.Join("\n", lines).Trim().Replace("*/", "* /", StringComparison.Ordinal);
        if (text.Length == 0) return "";
        return !text.Contains('\n') ? pad + "/** " + text + " */\n" : pad + "/**\n" + string.Join("\n", text.Split('\n').Select(line => pad + " * " + line.TrimEnd())) + "\n" + pad + " */\n";
    }
}
