namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Finite USD scalar from packages/schema/src/money.ts.
/// </summary>
[JsonConverter(typeof(MoneyJsonConverter))]
[Vogen.ValueObject<double>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct Money
{
    public double Amount => Value;
    public void Deconstruct(out double amount) => amount = Amount;
    public static Money FromExisting(double value) => From(FiniteNumberJsonConverter.Validate(value));
    private static Vogen.Validation Validate(double value) => double.IsFinite(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Expected finite number.");
    public static readonly Money Zero = FromExisting(0.0);

    public override string ToString() => $"${Amount:F4}";
    public static implicit operator double(Money m) => m.Amount;
    public static explicit operator Money(double amount) => FromExisting(amount);
}

public sealed class MoneyJsonConverter() : ScalarJsonConverter<Money, double>(Money.FromExisting, static value => value.Amount)
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Money.FromExisting(FiniteNumberJsonConverter.ReadValue(ref reader));

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
        FiniteNumberJsonConverter.WriteValue(writer, value.Amount);
}
