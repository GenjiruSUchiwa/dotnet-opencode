namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

public sealed class WorktreeTrimmedStringConverter : JsonConverter<string>
{
    public override bool HandleNull => true;
    public static string Normalize(string value)
    {
        if (value is null) throw new JsonException("Worktree strategy/branch must be a string.");
        const string space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        var text = Regex.Replace(value, @"\A[" + space + @"]+|[" + space + @"]+\z", "");
        return text.Length > 0 ? text : throw new JsonException("Worktree strategy/branch must not be empty.");
    }
    public override string Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType == JsonTokenType.String
        ? Normalize(reader.GetString()!) : throw new JsonException("Worktree strategy/branch must be a string.");
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(Normalize(value));
}
