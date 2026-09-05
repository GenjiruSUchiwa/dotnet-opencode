namespace OpenCode.Cli.CommandLine;

using System.CommandLine.Parsing;
using System.Globalization;
using System.Numerics;
using OpenCode.Client;

/// <summary>Pure option-value converters used by System.CommandLine, not argv parsers.</summary>
internal static class CliValueParsers
{
    private static readonly char[] Whitespace = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    internal static Dictionary<string, string> Headers(ArgumentResult result)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in result.Tokens)
        {
            var index = token.Value.IndexOf(':');
            if (index < 1) { result.AddError("Invalid header, expected name:value."); continue; }
            var name = token.Value[..index].Trim(Whitespace);
            var value = token.Value[(index + 1)..].Trim(Whitespace);
            try { ApiHttpClient.ValidateHeader(name, value); }
            catch (ArgumentException error) { result.AddError(error.Message); continue; }
            values[name] = value;
        }
        return values;
    }
    internal static Dictionary<string, string> Parameters(ArgumentResult result)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in result.Tokens)
        {
            var pair = token.Value.Split('=');
            if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
            { result.AddError("Invalid key=value format. Exactly one '=' and nonempty key/value are required."); continue; }
            values[pair[0]] = pair[1];
        }
        return values;
    }
    internal static long? Integer(ArgumentResult result, long minimum, long maximum)
    {
        // Effect Param.parseFlag selects the first occurrence for scalar flags.
        var text = result.Tokens[0].Value.Trim(Whitespace);
        double number;
        if (text.Length == 0) number = 0;
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2
            && BigInteger.TryParse("0" + text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) number = (double)hex;
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) || text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            var radix = char.ToLowerInvariant(text[1]) == 'b' ? 2 : 8;
            var digits = text[2..];
            if (digits.Length == 0 || digits.Any(value => value < '0' || value >= '0' + radix)) { result.AddError("Expected an integer."); return null; }
            number = (double)digits.Aggregate(BigInteger.Zero, (sum, value) => sum * radix + (value - '0'));
        }
        else if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number))
        { result.AddError("Expected an integer."); return null; }
        if (!double.IsFinite(number) || Math.Truncate(number) != number || number < minimum || number > maximum || number >= 9223372036854775808d)
        { result.AddError($"Expected an integer from {minimum} to {maximum}."); return null; }
        return (long)number;
    }
    internal static void Server(OptionResult result)
    {
        if (result.GetValueOrDefault<string?>() is { } value && !IsHttpUrl(value, originOnly: true))
            result.AddError("--server requires an HTTP(S) origin without credentials, path, query, or fragment.");
    }
    internal static bool IsHttpUrl(string value, bool originOnly) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && (!originOnly || uri.AbsolutePath == "/");
}
