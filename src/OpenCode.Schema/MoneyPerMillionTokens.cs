namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Money.USDPerMillionTokens, distinct from total USD cost.</summary>
[JsonConverter(typeof(MoneyPerMillionTokensJsonConverter))]
[Vogen.ValueObject<double>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct MoneyPerMillionTokens
{
    public double Amount => Value;
    public void Deconstruct(out double amount) => amount = Amount;
    public override string ToString() => $"MoneyPerMillionTokens {{ Amount = {Amount} }}";
    public static MoneyPerMillionTokens FromExisting(double value) => From(FiniteNumberJsonConverter.Validate(value));
    private static Vogen.Validation Validate(double value) => double.IsFinite(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("Expected finite number.");
    public static readonly MoneyPerMillionTokens Zero = FromExisting(0);
    public static implicit operator double(MoneyPerMillionTokens value) => value.Amount;
    public static explicit operator MoneyPerMillionTokens(double value) => FromExisting(value);
}

public sealed class MoneyPerMillionTokensJsonConverter() : ScalarJsonConverter<MoneyPerMillionTokens, double>(MoneyPerMillionTokens.FromExisting, static value => value.Amount)
{
    public override MoneyPerMillionTokens Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MoneyPerMillionTokens.FromExisting(FiniteNumberJsonConverter.ReadValue(ref reader));
    public override void Write(Utf8JsonWriter writer, MoneyPerMillionTokens value, JsonSerializerOptions options) =>
        FiniteNumberJsonConverter.WriteValue(writer, value.Amount);
}
