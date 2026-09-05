namespace OpenCode.Core.Llm;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

internal static class CatalogNumbers
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new IntegerNumberJsonConverter() } };

    internal static double Integer(JsonNode value) => JsonSerializer.Deserialize<double>(value.ToJsonString(), Options);

    internal static double Finite(JsonNode value)
    {
        if (value is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number)
            throw new JsonException("Catalog values must be JSON numbers.");

        // Parsed JSON and constructed JsonValues can have different CLR backing types.
        var number = scalar.TryGetValue<double>(out var floating) ? floating
            : scalar.TryGetValue<long>(out var signed) ? signed
            : scalar.TryGetValue<int>(out var integer) ? integer
            : scalar.TryGetValue<ulong>(out var unsigned) ? unsigned
            : scalar.TryGetValue<decimal>(out var precise) ? (double)precise
            : JsonSerializer.Deserialize<double>(scalar.ToJsonString());
        return double.IsFinite(number) ? number : throw new JsonException("Catalog numbers must be finite.");
    }
}
