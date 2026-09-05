namespace OpenCode.Schema;

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>DurationFromString representation: milliseconds, nanoseconds, or signed infinity.</summary>
[JsonConverter(typeof(ConfigDurationJsonConverter))]
public abstract partial record ConfigDuration
{
    private protected ConfigDuration() { }
    public sealed record Milliseconds : ConfigDuration
    {
        public double Value { get; }
        internal Milliseconds(double value) => Value = value;
    }
    public sealed record Nanoseconds : ConfigDuration
    {
        public BigInteger Value { get; }
        internal Nanoseconds(BigInteger value) => Value = value;
    }
    public sealed record Infinite(bool Negative) : ConfigDuration;

    [GeneratedRegex(@"\A(?<number>-?[0-9]+(?:\.[0-9]+)?)[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+(?<unit>nanos?|micros?|millis?|seconds?|minutes?|hours?|days?|weeks?)\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex Syntax();

    public static ConfigDuration Parse(string input)
    {
        if (input == "Infinity") return new Infinite(false);
        if (input == "-Infinity") return new Infinite(true);
        var match = Syntax().Match(PromptValidation.Required(input));
        if (!match.Success) throw new JsonException("Invalid duration string.");
        var text = match.Groups["number"].Value;
        var unit = match.Groups["unit"].Value;
        if (unit is "nano" or "nanos" or "micro" or "micros")
        {
            var scale = unit is "micro" or "micros" ? 1000 : 1;
            var nanos = text.Contains('.') ? Round(double.Parse(text, CultureInfo.InvariantCulture) * scale)
                : BigInteger.Parse(text, CultureInfo.InvariantCulture) * scale;
            return nanos.IsZero ? new Milliseconds(0) : new Nanoseconds(nanos);
        }
        var factor = unit switch
        {
            "milli" or "millis" => 1, "second" or "seconds" => 1000, "minute" or "minutes" => 60_000,
            "hour" or "hours" => 3_600_000, "day" or "days" => 86_400_000, _ => 604_800_000
        };
        var millis = double.Parse(text, CultureInfo.InvariantCulture) * factor;
        if (double.IsInfinity(millis)) return new Infinite(millis < 0);
        if (millis == 0) return new Milliseconds(0);
        return Math.Truncate(millis) == millis ? new Milliseconds(millis) : new Nanoseconds(Round(millis * 1_000_000));
    }

    private static BigInteger Round(double value)
    {
        var rounded = value < 0 ? Math.Ceiling(value - 0.5) : Math.Floor(value + 0.5);
        if (!double.IsFinite(rounded)) throw new JsonException("Duration nanoseconds cannot be represented from this fractional input.");
        return new BigInteger(rounded);
    }

    public string Encode() => this switch
    {
        Infinite infinity => infinity.Negative ? "-Infinity" : "Infinity",
        Nanoseconds nanos => nanos.Value.ToString(CultureInfo.InvariantCulture) + " nanos",
        Milliseconds millis => Number(millis.Value) + " millis",
        _ => throw new JsonException("Unknown duration representation.")
    };

    private static string Number(double value)
    {
        if (value == 0) return "0";
        var negative = value < 0;
        var number = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var marker = number.IndexOf('E');
        if (marker < 0) return negative ? "-" + number : number;
        var exponent = int.Parse(number.AsSpan(marker + 1), CultureInfo.InvariantCulture);
        var sign = negative ? "-" : "";
        if (exponent < 21)
        {
            var digits = number[..marker].Replace(".", "", StringComparison.Ordinal);
            return sign + digits + new string('0', exponent + 1 - digits.Length);
        }
        return $"{sign}{number[..marker]}e+{exponent.ToString(CultureInfo.InvariantCulture)}";
    }
}
