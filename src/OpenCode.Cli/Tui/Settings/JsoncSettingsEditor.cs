namespace OpenCode.Cli.Tui.Settings;

using System.Text;
using System.Text.Json;

/// <summary>Edits one property value or inserts/removes one property without reserializing its enclosing objects.</summary>
public static class JsoncSettingsEditor
{
    private sealed record Property(string Name, int Start, Node Value);
    private sealed record Node(int Start, int End, JsonTokenType Type, IReadOnlyList<Property> Properties);
    private readonly record struct Edit(int Start, int Length, string Text);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text.AsMemory(text.StartsWith('\uFEFF') ? 1 : 0),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("CLI configuration must be an object.");
        return document.RootElement.Clone();
    }

    public static string Set(string text, IReadOnlyList<string> path, JsonElement? value)
    {
        if (path.Count == 0 || path.Any(string.IsNullOrEmpty)) throw new ArgumentException("A setting requires a nonempty property path.", nameof(path));
        Parse(text);
        var bytes = Utf8.GetBytes(text);
        var bom = text.StartsWith('\uFEFF') ? 3 : 0;
        var reader = new Utf8JsonReader(bytes.AsSpan(bom), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        reader.Read();
        var root = ReadNode(ref reader, bytes, bom);
        var current = root;
        for (var depth = 0; depth < path.Count; depth++)
        {
            var found = current.Properties.Where(property => property.Name == path[depth]).ToArray();
            if (found.Length > 1) throw new JsonException($"Setting path '{string.Join('.', path.Take(depth + 1))}' is duplicated; no configuration was changed.");
            var property = found.SingleOrDefault();
            if (property is null)
            {
                if (value is null) return text;
                var branch = value.Value.GetRawText();
                for (var index = path.Count - 1; index > depth; index--)
                    branch = "{ " + JsonSerializer.Serialize(path[index]) + ": " + branch + " }";
                return Insert(text, current, path[depth], branch);
            }
            if (depth < path.Count - 1)
            {
                if (property.Value.Type != JsonTokenType.StartObject)
                    throw new JsonException($"Setting parent '{string.Join('.', path.Take(depth + 1))}' must be an object; its contents were not overwritten.");
                current = property.Value;
                continue;
            }
            if (value is not null) return Apply(text, [new(property.Value.Start, property.Value.End - property.Value.Start, value.Value.GetRawText())]);
            var edits = new List<Edit> { new(property.Start, property.Value.End - property.Start, "") };
            var position = current.Properties.ToList().IndexOf(property);
            var comma = Comma(text, property.Value.End, position + 1 < current.Properties.Count ? current.Properties[position + 1].Start : current.End - 1);
            if (comma < 0 && position > 0) comma = Comma(text, current.Properties[position - 1].Value.End, property.Start);
            if (comma >= 0) edits.Add(new(comma, 1, ""));
            return Apply(text, edits);
        }
        return text;
    }

    private static Node ReadNode(ref Utf8JsonReader reader, byte[] source, int bom)
    {
        var type = reader.TokenType;
        var start = Utf8.GetCharCount(source.AsSpan(0, checked((int)reader.TokenStartIndex) + bom));
        var properties = new List<Property>();
        if (type == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a property name.");
                var name = reader.GetString()!;
                var keyStart = Utf8.GetCharCount(source.AsSpan(0, checked((int)reader.TokenStartIndex) + bom));
                if (!reader.Read()) throw new JsonException("Missing property value.");
                properties.Add(new(name, keyStart, ReadNode(ref reader, source, bom)));
            }
        }
        else if (type == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) ReadNode(ref reader, source, bom);
        }
        return new(start, Utf8.GetCharCount(source.AsSpan(0, checked((int)reader.BytesConsumed) + bom)), type, properties);
    }

    private static string Insert(string text, Node parent, string key, string value)
    {
        var close = parent.End - 1;
        var line = text.LastIndexOf('\n', close);
        var ownLine = line >= 0 && text.AsSpan(line + 1, close - line - 1).Trim().IsEmpty;
        var indent = ownLine ? text[(line + 1)..close] : Indentation(text, parent.Start);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertion = ownLine ? line + 1 : close;
        var edits = new List<Edit>();
        if (parent.Properties.LastOrDefault() is { } last && Comma(text, last.Value.End, close) < 0)
            edits.Add(new(last.Value.End, 0, ","));
        var property = indent + "  " + JsonSerializer.Serialize(key) + ": " + value;
        // If the previous value abuts '}', both insertions share an offset.
        // Insert the new property first so the separating comma stays before it.
        edits.Insert(0, new(insertion, 0, (ownLine ? "" : newline) + property + newline + (ownLine ? "" : indent)));
        return Apply(text, edits);
    }

    private static string Indentation(string text, int position)
    {
        var start = text.LastIndexOf('\n', position) + 1;
        var end = start;
        while (end < position && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }

    private static int Comma(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] == ',') return index;
            if (text[index] != '/' || index + 1 >= end) continue;
            if (text[index + 1] == '/')
            {
                while (index < end && text[index] != '\n') index++;
            }
            else if (text[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < end && !(text[index] == '*' && text[index + 1] == '/')) index++;
                index++;
            }
        }
        return -1;
    }

    private static string Apply(string text, IEnumerable<Edit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Start)) text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        Parse(text);
        return text;
    }
}
