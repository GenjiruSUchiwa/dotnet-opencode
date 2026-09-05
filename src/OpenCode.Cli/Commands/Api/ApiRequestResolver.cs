namespace OpenCode.Cli.Commands.Api;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record ApiRequest(string Method, string Path);

public static class ApiRequestResolver
{
    public static ApiRequest? Raw(IReadOnlyList<string> input) => input.Count == 2
        && Method(input[0].ToLowerInvariant()) && input[1].StartsWith('/')
        ? new(input[0].ToUpperInvariant(), input[1]) : null;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Preserve source CLI error text printed for an unknown operation.")]
    public static ApiRequest Operation(JsonElement spec, string operationId, IReadOnlyDictionary<string, string> parameters)
    {
        if (spec.ValueKind != JsonValueKind.Object) throw new JsonException("OpenAPI document must be an object.");
        if (spec.TryGetProperty("paths", out var paths) && paths.ValueKind != JsonValueKind.Null)
        {
            if (paths.ValueKind != JsonValueKind.Object) throw new JsonException("OpenAPI paths must be an object.");
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var operation in path.Value.EnumerateObject())
                    if (Method(operation.Name) && operation.Value.ValueKind == JsonValueKind.Object
                        && operation.Value.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String
                        && id.GetString() == operationId)
                        return new(operation.Name.ToUpperInvariant(), Interpolate(path.Name, parameters));
            }
        }
        throw new ArgumentException($"Operation not found: {operationId}");
    }

    private static bool Method(string value) => value is "delete" or "get" or "head" or "options" or "patch" or "post" or "put";
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The source error identifies the actual missing path parameter, not this helper's C# argument.")]
    private static string Interpolate(string path, IReadOnlyDictionary<string, string> parameters)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pathname = Regex.Replace(path, @"\{(?<name>[^}]+)\}", match =>
        {
            var name = match.Groups["name"].Value;
            if (!parameters.TryGetValue(name, out var value)) throw new ArgumentException($"Missing path parameter: {name}");
            used.Add(name);
            return Encode(value, false);
        }, RegexOptions.NonBacktracking);
        // Object.entries enumerates integer-index names first, then other names in
        // insertion order. URLSearchParams uses '+' for spaces and escapes '~'.
        var ordered = parameters.OrderBy(pair => Index(pair.Key) is null ? 1 : 0).ThenBy(pair => Index(pair.Key) ?? 0);
        var query = string.Join('&', ordered.Where(pair => !used.Contains(pair.Key)).Select(pair => Encode(pair.Key, true) + "=" + Encode(pair.Value, true)));
        return query.Length == 0 ? pathname : pathname + "?" + query;
    }
    private static uint? Index(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value != uint.MaxValue && value.ToString(CultureInfo.InvariantCulture) == key ? value : null;
    private static string Encode(string value, bool query)
    {
        var bytes = (query ? Encoding.UTF8 : new UTF8Encoding(false, true)).GetBytes(value);
        var result = new StringBuilder();
        foreach (var item in bytes)
        {
            var character = (char)item;
            if (char.IsAsciiLetterOrDigit(character) || (query ? "*-._" : "-_.!~*'()").Contains(character)) result.Append(character);
            else if (query && item == 32) result.Append('+');
            else result.Append('%').Append(item.ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
}
