namespace OpenCode.Core.Commands;

using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

/// <summary>ConfigCommandPlugin.evaluateTemplate, including interpolation in expanded user arguments.</summary>
public static class CommandTemplate
{
    private const string Space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private static readonly Regex Arguments = new("(?:\\[Image[" + Space + "]+[0-9]+\\]|\"[^\"]*\"|'[^']*'|[^" + Space + "\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Placeholder = new(@"\$(?<position>[0-9]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Shell = new("!`(?<command>[^`]+)`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string[] ParseArguments(string input) => Arguments.Matches(input)
        .Select(match => Regex.Replace(match.Value, "^[\"']|[\"']$", "", RegexOptions.NonBacktracking)).ToArray();

    internal static string Trim(string value) => Regex.Replace(value, @"\A[" + Space + @"]+|[" + Space + @"]+\z", "", RegexOptions.NonBacktracking);

    public static async Task<string> EvaluateAsync(string template, string input, Func<string, Task<string>> interpolate,
        CancellationToken ct = default)
    {
        var args = ParseArguments(input);
        var placeholders = Placeholder.Matches(template);
        var last = placeholders.Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
        var expanded = Placeholder.Replace(template, match =>
        {
            var position = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (position - 1 >= args.Length) return "";
            // JS args[-1] is undefined, except the highest $0 uses slice(-1).
            if (position == last) return string.Join(" ", args.Skip(position == 0 ? Math.Max(0, args.Length - 1) : (int)position - 1));
            return position == 0 ? "undefined" : args[(int)position - 1];
        });
        // JS String.replaceAll with a string replacement interprets $$, $&, $`, and $'.
        var text = Regex.Replace(expanded, Regex.Escape("$ARGUMENTS"), match => ExpandReplacement(input, expanded, match), RegexOptions.NonBacktracking);
        text = placeholders.Count == 0 && !template.Contains("$ARGUMENTS", StringComparison.Ordinal) && Trim(input).Length > 0
            ? Trim(text + "\n\n" + input) : Trim(text);
        var matches = Shell.Matches(text);
        if (matches.Count == 0) return text;
        using var concurrency = new SemaphoreSlim(2);
        var outputs = await Task.WhenAll(matches.Select(async match =>
        {
            // Interpolation is caller-supplied; retain its original callback context.
            await concurrency.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                return await interpolate(match.Groups[1].Value).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Shell interpolation failed for {JsonSerializer.Serialize(match.Groups[1].Value, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}: {error.Message}", error);
            }
            finally { concurrency.Release(); }
        })).ConfigureAwait(true);
        var index = 0;
        return Shell.Replace(text, _ => outputs[index++]);
    }

    private static string ExpandReplacement(string replacement, string source, Match match) =>
        Regex.Replace(replacement, @"\$(?<token>[$&`'])", token => token.Groups[1].Value switch
        {
            "$" => "$", "&" => match.Value, "`" => source[..match.Index], "'" => source[(match.Index + match.Length)..], _ => token.Value
        }, RegexOptions.NonBacktracking);
}
