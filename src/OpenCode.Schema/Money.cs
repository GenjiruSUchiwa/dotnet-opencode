namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Money.USD from packages/schema/src/money.ts
/// </summary>
[JsonConverter(typeof(MoneyJsonConverter))]
public readonly record struct Money(double Amount)
{
    public static readonly Money Zero = new(0.0);

    public override string ToString() => $"${Amount:F4}";
    public static implicit operator double(Money m) => m.Amount;
    public static explicit operator Money(double amount) => new(amount);
}

public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Amount);
}
