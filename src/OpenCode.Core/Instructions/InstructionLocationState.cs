namespace OpenCode.Core.Instructions;

using System.Text.Json.Nodes;

/// <summary>
/// Location-owned last-good producer observations. The owning SessionStore disposes
/// or explicitly invalidates this scope; durable instruction values remain in SQLite.
/// </summary>
internal sealed class InstructionLocationState(string directory) : IDisposable
{
    internal string Directory { get; } = directory;
    private readonly Lock _sync = new();
    private (string Home, string Global)? _scope;
    private IReadOnlyList<(string? Path, JsonObject Info)>? _documents;
    private bool _disposed;

    internal IReadOnlyList<(string? Path, JsonObject Info)> Observe(string home, string global,
        IReadOnlyList<(string? Path, JsonObject Info)> documents, bool available)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scope != (home, global)) { _scope = (home, global); _documents = null; }
            if (available)
            {
                return documents;
            }
            return _documents ?? throw new InstructionInitializationBlockedException(["core/skill-guidance", "core/reference-guidance"]);
        }
    }

    internal void Remember(IReadOnlyList<(string? Path, JsonObject Info)> documents)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _documents = documents.Select(document => (document.Path, Retain(document.Info))).ToArray();
        }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _documents = null; _scope = null; }
    }

    private static JsonObject Retain(JsonObject document)
    {
        var result = new JsonObject();
        // Providers, credentials, headers, arbitrary bodies, and unrelated config
        // never belong in an instruction observation cache.
        foreach (var key in new[] { "skills", "references", "permissions", "permission", "tools" })
            if (document.TryGetPropertyValue(key, out var value)) result[key] = value?.DeepClone();
        if (document["references"] is JsonObject references)
        {
            var observed = new JsonObject();
            foreach (var reference in references)
                observed[reference.Key] = reference.Value is JsonObject
                    ? ScalarFields(reference.Value, "path", "description", "hidden") : reference.Value?.DeepClone();
            result["references"] = observed;
        }
        if (document.ContainsKey("permissions")) result["permissions"] = Rules(document["permissions"]);
        foreach (var key in new[] { "agent", "mode", "instructions", "plugins", "plugin", "mcp" })
            if (document.TryGetPropertyValue(key, out var value)) result[key] = Presence(value);
        if (document["agents"] is JsonObject agents)
        {
            var observed = new JsonObject();
            foreach (var agent in agents)
            {
                if (agent.Value is not JsonObject fields) { observed[agent.Key] = false; continue; }
                var item = new JsonObject();
                foreach (var field in fields)
                    item[field.Key] = field.Key == "permissions" ? Rules(field.Value) :
                        field.Key is "system" or "description" or "color" or "hidden"
                            ? field.Value?.DeepClone() : Presence(field.Value);
                observed[agent.Key] = item;
            }
            result["agents"] = observed;
        }
        else if (document.ContainsKey("agents")) result["agents"] = false;
        return result;
    }

    private static JsonNode? Rules(JsonNode? value) => value is JsonArray rules
        ? new JsonArray(rules.Select(rule => ScalarFields(rule, "action", "resource", "effect")).ToArray())
        : value is null ? null : JsonValue.Create(false);

    private static JsonNode? ScalarFields(JsonNode? value, params string[] fields)
    {
        if (value is not JsonObject map) return value is null ? null : JsonValue.Create(false);
        var result = new JsonObject();
        foreach (var field in fields)
            if (map.TryGetPropertyValue(field, out var item))
                result[field] = item is JsonValue ? item.DeepClone() : item is null ? null : JsonValue.Create(false);
        return result;
    }

    private static JsonNode? Presence(JsonNode? value) => value switch
    {
        null => null,
        JsonArray { Count: 0 } => new JsonArray(),
        JsonObject { Count: 0 } => new JsonObject(),
        _ => JsonValue.Create(true)
    };
}
