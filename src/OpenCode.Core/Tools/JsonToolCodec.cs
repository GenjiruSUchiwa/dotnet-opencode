namespace OpenCode.Core.Tools;

using System.Text.Json;

/// <summary>Explicit JSON Schema subset, not a permissive fallback for arbitrary JSON Schema.
/// Unsupported assertion keywords fail construction. Decode preserves JSON values; Encode validates serialized JSON.</summary>
public sealed class JsonToolCodec : IToolValueCodec
{
    private static readonly JsonSerializerOptions Serialization = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HashSet<string> Types = ["object", "array", "string", "number", "integer", "boolean", "null"];
    public JsonElement JsonSchema { get; }

    public JsonToolCodec(JsonElement schema)
    {
        CheckSchema(schema, "root", 0);
        JsonSchema = schema.Clone();
    }

    public ValueTask<object?> DecodeAsync(JsonElement value, CancellationToken ct)
    {
        Validate(value, ct);
        return ValueTask.FromResult<object?>(value.Clone());
    }

    public ValueTask<JsonElement> EncodeAsync(object? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        JsonElement json;
        try { json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, Serialization); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ToolValidationException([new("root", $"Output is not serializable JSON: {error.Message}")]);
        }
        Validate(json, ct);
        return ValueTask.FromResult(json.Clone());
    }

    private void Validate(JsonElement value, CancellationToken ct)
    {
        var issues = new List<ToolValidationIssue>();
        CheckValue(JsonSchema, value, "root", issues, ct, 0);
        if (issues.Count > 0) throw new ToolValidationException(issues.AsReadOnly());
    }

    private static void CheckSchema(JsonElement schema, string path, int depth)
    {
        if (depth > 64) throw new NotSupportedException("Tool schema exceeds 64 levels.");
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{path}: schema must be an object or boolean.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in schema.EnumerateObject())
        {
            if (!names.Add(item.Name)) throw new ArgumentException($"{path}: duplicate schema keyword {item.Name}.");
            var value = item.Value;
            switch (item.Name)
            {
                case "title": case "description": case "$comment":
                    if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{path}.{item.Name} must be a string.");
                    break;
                case "examples":
                    if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{path}.examples must be an array.");
                    break;
                case "default":
                    // Annotations do not apply defaults or otherwise change validation.
                    break;
                case "$schema":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not
                        ("https://json-schema.org/draft/2020-12/schema" or "http://json-schema.org/draft-07/schema#"))
                        throw new NotSupportedException("Only the documented draft-07/2020-12 subset is supported.");
                    break;
                case "type":
                    var types = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
                    if (types.Length == 0 || types.Any(type => type.ValueKind != JsonValueKind.String || !Types.Contains(type.GetString()!)))
                        throw new ArgumentException($"{path}.type contains an unsupported type.");
                    break;
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{path}.properties must be an object.");
                    var properties = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!properties.Add(property.Name)) throw new ArgumentException($"{path}.properties contains duplicate property {property.Name}.");
                        CheckSchema(property.Value, path + "." + property.Name, depth + 1);
                    }
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                        throw new ArgumentException($"{path}.required must be an array of strings.");
                    break;
                case "additionalProperties": case "items": case "not":
                    CheckSchema(value, path + "." + item.Name, depth + 1);
                    break;
                case "allOf": case "anyOf": case "oneOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                        throw new ArgumentException($"{path}.{item.Name} must be a nonempty array.");
                    foreach (var branch in value.EnumerateArray()) CheckSchema(branch, path + "." + item.Name, depth + 1);
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) throw new ArgumentException($"{path}.enum must be a nonempty array.");
                    break;
                case "const": break;
                case "minLength": case "maxLength": case "minItems": case "maxItems": case "minProperties": case "maxProperties":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0) throw new ArgumentException($"{path}.{item.Name} must be a nonnegative 32-bit integer.");
                    break;
                case "minimum": case "maximum": case "exclusiveMinimum": case "exclusiveMaximum":
                    if (!TryNumber(value, out _))
                        throw new ArgumentException($"{path}.{item.Name} must be exactly representable as a .NET decimal.");
                    break;
                default: throw new NotSupportedException($"Unsupported tool schema keyword: {path}.{item.Name}");
            }
        }
    }

    private static void CheckValue(JsonElement schema, JsonElement value, string path, List<ToolValidationIssue> issues, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (issues.Count >= 100) return;
        if (depth > 64) { issues.Add(new(path, "Value exceeds 64 levels.")); return; }
        if (value.ValueKind == JsonValueKind.Undefined) { issues.Add(new(path, "Value must be JSON.")); return; }
        if (schema.ValueKind == JsonValueKind.False) { issues.Add(new(path, "Value is forbidden by the schema.")); return; }
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.TryGetProperty("type", out var type))
        {
            var valid = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Any(item => Matches(item.GetString()!, value)) : Matches(type.GetString()!, value);
            if (!valid) { issues.Add(new(path, $"Expected type {type.GetRawText()}.")); return; }
        }
        if (value.ValueKind == JsonValueKind.Number && !TryNumber(value, out _))
        { issues.Add(new(path, "Numeric assertions require a value exactly representable as a .NET decimal.")); return; }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value)))
            issues.Add(new(path, "Value is not in enum."));
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value)) issues.Add(new(path, "Value differs from const."));
        foreach (var keyword in new[] { "allOf", "anyOf", "oneOf", "not" })
        {
            if (!schema.TryGetProperty(keyword, out var branches)) continue;
            var valid = 0;
            var candidates = keyword == "not" ? [branches] : branches.EnumerateArray().ToArray();
            foreach (var candidate in candidates)
            {
                var failures = new List<ToolValidationIssue>();
                CheckValue(candidate, value, path, failures, ct, depth + 1);
                if (failures.Count == 0) valid++;
            }
            if (keyword switch { "allOf" => valid != candidates.Length, "anyOf" => valid == 0, "oneOf" => valid != 1, _ => valid != 0 })
                issues.Add(new(path, $"Value does not satisfy {keyword}."));
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = schema.TryGetProperty("properties", out var declared) ? declared : default;
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _)) issues.Add(new(path + "." + name.GetString(), "Required property is missing."));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) issues.Add(new(path + "." + property.Name, "Duplicate JSON properties are not supported."));
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                    CheckValue(child, property.Value, path + "." + property.Name, issues, ct, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                    CheckValue(additional, property.Value, path + "." + property.Name, issues, ct, depth + 1);
            }
            Count("Properties", seen.Count);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            Count("Items", value.GetArrayLength());
            if (schema.TryGetProperty("items", out var itemSchema))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) CheckValue(itemSchema, item, $"{path}[{index++}]", issues, ct, depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.String) Count("Length", value.GetString()!.EnumerateRunes().Count());
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDecimal();
            foreach (var bound in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
                if (schema.TryGetProperty(bound, out var limit) && (bound switch
                { "minimum" => number < limit.GetDecimal(), "maximum" => number > limit.GetDecimal(), "exclusiveMinimum" => number <= limit.GetDecimal(), _ => number >= limit.GetDecimal() }))
                    issues.Add(new(path, $"Value violates {bound} {limit.GetRawText()}."));
        }

        void Count(string suffix, int count)
        {
            if (schema.TryGetProperty("min" + suffix, out var min) && count < min.GetInt32()) issues.Add(new(path, $"Value violates min{suffix} {min}."));
            if (schema.TryGetProperty("max" + suffix, out var max) && count > max.GetInt32()) issues.Add(new(path, $"Value violates max{suffix} {max}."));
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => TryNumber(value, out var number) && decimal.Truncate(number) == number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static bool TryNumber(JsonElement value, out decimal number)
    {
        number = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number) &&
            JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(number));
    }
}
