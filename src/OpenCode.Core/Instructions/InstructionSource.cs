namespace OpenCode.Core.Instructions;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal enum InstructionAvailability { Available, Removed, Unavailable }

// The ordered list is composed by the Session, not a process-wide registry.
internal sealed record InstructionSource(string Key, InstructionAvailability Availability, JsonElement Value,
    Func<JsonElement, string> Initial, Func<JsonElement, JsonElement, string> Changed, Func<JsonElement, string>? Removed = null);

public sealed class InstructionInitializationBlockedException(IReadOnlyList<string> keys)
    : InvalidOperationException("Instruction initialization blocked by unavailable sources: " + string.Join(", ", keys))
{
    public IReadOnlyList<string> Keys { get; } = keys;
}

/// <summary>Instructions.canonical/hash: sorted UTF-16 object keys, array order, JSON.stringify scalar encoding.</summary>
internal static class InstructionJson
{
    internal static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Stringify(value, true))));

    internal static string Stringify(JsonElement value, bool sorted = false, bool pretty = false)
    {
        var output = new StringBuilder();
        Write(value, output, sorted, pretty, 0);
        return output.ToString();
    }

    private static void Write(JsonElement value, StringBuilder output, bool sorted, bool pretty, int depth)
    {
        void Line() { if (pretty) output.Append('\n').Append(' ', depth * 2); }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                output.Append('{');
                // JsonElement retains duplicate members, unlike the source's parsed JS
                // object: the last value wins without moving the first insertion position.
                var properties = value.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal)
                    .Select(group => group.Last()).ToArray();
                var ordered = sorted ? properties.OrderBy(property => property.Name, StringComparer.Ordinal) :
                    properties.OrderBy(property => Index(property.Name)).ThenBy(property => Index(property.Name) == uint.MaxValue ? Array.IndexOf(properties, property) : 0);
                var count = 0;
                foreach (var property in ordered)
                {
                    if (count++ > 0) output.Append(',');
                    if (pretty) output.Append('\n').Append(' ', (depth + 1) * 2);
                    Quote(property.Name, output);
                    output.Append(pretty ? ": " : ":");
                    Write(property.Value, output, sorted, pretty, depth + 1);
                }
                if (count > 0) Line();
                output.Append('}');
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ > 0) output.Append(',');
                    if (pretty) output.Append('\n').Append(' ', (depth + 1) * 2);
                    Write(item, output, sorted, pretty, depth + 1);
                }
                if (index > 0) Line();
                output.Append(']');
                break;
            case JsonValueKind.String: Quote(value.GetString()!, output); break;
            case JsonValueKind.Number: output.Append(Number(value.GetDouble())); break;
            case JsonValueKind.True: output.Append("true"); break;
            case JsonValueKind.False: output.Append("false"); break;
            case JsonValueKind.Null: output.Append("null"); break;
            default: throw new JsonException("Instruction value must be canonical JSON.");
        }
    }

    private static uint Index(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && index != uint.MaxValue && key == index.ToString(CultureInfo.InvariantCulture) ? index : uint.MaxValue;

    private static void Quote(string text, StringBuilder output)
    {
        output.Append('"');
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\b': output.Append("\\b"); break;
                case '\f': output.Append("\\f"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default:
                    if (char.IsHighSurrogate(ch) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                        output.Append(ch).Append(text[++index]);
                    else if (ch < 0x20 || char.IsSurrogate(ch)) output.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else output.Append(ch);
                    break;
            }
        }
        output.Append('"');
    }

    private static string Number(double number)
    {
        if (!double.IsFinite(number)) throw new JsonException("Instruction numbers must be finite.");
        if (number == 0) return "0";
        var negative = number < 0 ? "-" : "";
        var text = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture);
        var parts = text.Split('E');
        var point = parts[0].IndexOf('.');
        var digits = parts[0].Replace(".", "");
        var position = (point < 0 ? digits.Length : point) + (parts.Length == 1 ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture));
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; position--; }
        if (position > 0 && position <= 21)
            return negative + (position >= digits.Length ? digits.PadRight(position, '0') : digits.Insert(position, "."));
        if (position <= 0 && position > -6) return negative + "0." + new string('0', -position) + digits;
        var exponent = position - 1;
        return negative + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" +
            (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
