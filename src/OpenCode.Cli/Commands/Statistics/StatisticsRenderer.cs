namespace OpenCode.Cli.Commands.Statistics;

using System.Globalization;
using System.Numerics;
using OpenCode.Schema;

/// <summary>Source stats.ts line report, not an interactive terminal renderer.</summary>
public static class StatisticsRenderer
{
    public static string Render(SessionStatsInfo stats, StatisticsOptions options, string label, string scope,
        int width, bool color, TimeZoneInfo zone)
    {
        var palette = Palette();
        string Style(string value, string code) => color ? $"\x1b[{code}m{value}\x1b[0m" : value;
        string Metric(double count, string noun) => $"{Style(Number(count), "1;" + palette.Primary)} {noun}{(count == 1 ? "" : "s")}";
        var totals = stats.Tools switch { SessionStatsToolsSummary summary => summary.Totals, SessionStatsToolsDetail detail => detail.Totals, _ => null };
        var terminal = totals is null ? 0d : (double)totals.Succeeded + totals.Failed;
        var toolSummary = totals is null ? "tool stats unavailable" : terminal == 0 ? "no tool calls"
            : $"{Style(Percent(totals.Succeeded / terminal * 100), "1;" + palette.Primary)} tool success";
        var details = options.Models || options.Tools || options.Cost || options.Full;
        var lines = new List<string>();
        if (details) lines.Add(Style($"{label} · {scope}", "2"));
        else
        {
            lines.Add($"{Style("dotnet opencode stats", "1;" + palette.Primary)} {Style($"· {label} · {scope}", "2")}");
            lines.Add("");
            if (stats.Sessions == 0 && stats.Prompts == 0 && stats.Steps == 0) lines.Add(Style("no activity in this range", "2"));
            else
            {
                lines.AddRange(Activity(stats, width, color, zone, palette));
                lines.Add("");
                lines.Add(Metric(stats.Sessions, "session") + (stats.Subagents > 0 ? " · " + Metric(stats.Subagents, "subagent") : ""));
                lines.Add($"{Metric(stats.Prompts, "prompt")} · {Metric(stats.Steps, "step")} · {Metric(Total(stats.Tokens), "token")}");
                lines.Add($"{toolSummary} · {Metric(stats.ActiveDays, "active day")} · best streak {Style(stats.Streak.ToString(CultureInfo.InvariantCulture), "1;" + palette.Primary)} day{(stats.Streak == 1 ? "" : "s")}");
            }
            lines.Add("");
            lines.Add(Style("opencode.ai", "2"));
        }
        if (options.Cost || options.Full)
        {
            var input = stats.Tokens.Input + stats.Tokens.Cache.Read + stats.Tokens.Cache.Write;
            lines.AddRange(["", "COST & TOKENS", Row("cost", "$" + Fixed(stats.Cost.Amount, 2)), Row("input", Number(stats.Tokens.Input)),
                Row("output", Number(stats.Tokens.Output)), Row("reasoning", Number(stats.Tokens.Reasoning)),
                Row("cache read", Number(stats.Tokens.Cache.Read)), Row("cache write", Number(stats.Tokens.Cache.Write)),
                Row("cached input", Percent(input == 0 ? 0 : stats.Tokens.Cache.Read / input * 100))]);
        }
        if (options.Models || options.Full)
        {
            lines.AddRange(["", "MODELS"]);
            if (stats.Models.Count == 0) lines.Add("  no model usage");
            else
            {
                var rows = stats.Models.Take((int)Math.Min(int.MaxValue, options.Limit)).ToArray();
                if (width >= 68) lines.Add(Table("model", "tokens", "steps", "cost"));
                foreach (var item in rows)
                {
                    var name = $"{item.Model.ProviderId}/{item.Model.Id}" + (string.IsNullOrEmpty(item.Model.Variant) ? "" : "#" + item.Model.Variant);
                    if (width >= 68) lines.Add(Table(name, Number(Total(item.Tokens)), Number(item.Steps), "$" + Fixed(item.Cost.Amount, 2)));
                    else lines.AddRange([Truncate(name, width), $"  {Number(Total(item.Tokens))} tokens · {Number(item.Steps)} steps · ${Fixed(item.Cost.Amount, 2)}"]);
                }
                var more = stats.Models.Count - rows.Length;
                if (more > 0) lines.AddRange(["", $"+{more.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} more model{(more == 1 ? "" : "s")}"]);
            }
        }
        if (options.Tools || options.Full)
        {
            lines.AddRange(["", "TOOL RELIABILITY"]);
            if (stats.Tools is not SessionStatsToolsDetail detail) lines.Add("  tool details unavailable");
            else if (detail.Usage.Count == 0) lines.Add("  no tool calls");
            else
            {
                var rows = detail.Usage.Take((int)Math.Min(int.MaxValue, options.Limit)).ToArray();
                if (width >= 68) lines.Add(Table("tool", "calls", "error", "p50"));
                foreach (var tool in rows)
                {
                    var finished = (double)tool.Succeeded + tool.Failed;
                    var error = finished == 0 ? "-" : Percent(tool.Failed / finished * 100);
                    var duration = tool.DurationP50 is { } value ? Duration(value) : "-";
                    if (width >= 68) lines.Add(Table(tool.Name, Number(tool.Calls), error, duration));
                    else lines.AddRange([Truncate(tool.Name, width), $"  {Number(tool.Calls)} calls · {error} error · {duration} p50"]);
                }
                lines.AddRange(["", $"{Number((double)detail.Totals.Succeeded + detail.Totals.Failed)} finished calls · {Number(detail.Totals.Unfinished)} unfinished"]);
                var more = detail.Usage.Count - rows.Length;
                if (more > 0) lines.Add($"+{more.ToString("N0", CultureInfo.GetCultureInfo("en-US"))} more tool{(more == 1 ? "" : "s")}");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static IEnumerable<string> Activity(SessionStatsInfo stats, int width, bool color, TimeZoneInfo zone,
        (string Primary, string[] Activity) palette)
    {
        string Style(string value, string code) => color ? $"\x1b[{code}m{value}\x1b[0m" : value;
        string Paint(int level) => !color ? "·░▒▓█"[level].ToString() : level == 0 ? "\x1b[2m·\x1b[22m"
            : $"\x1b[{palette.Activity[level - 1]}m{"·░▒▓█"[level]}\x1b[39m";
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stats.Range.From, zone).Date);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stats.Range.To.AddMilliseconds(-1), zone).Date);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var end = last.AddDays(6 - (((int)last.DayOfWeek + 6) % 7));
        var maxWeeks = Math.Max(1, Math.Min(53, width - 4));
        var totalWeeks = (end.DayNumber - start.DayNumber) / 7 + 1;
        var latest = end.AddDays(-(maxWeeks - 1) * 7);
        if (start < latest) start = latest; // Source uses end's Sunday for the cropped start.
        var weeks = Enumerable.Range(0, (end.DayNumber - start.DayNumber) / 7 + 1).Select(index => start.AddDays(index * 7)).ToArray();
        var labels = new List<char>();
        var previousMonth = -1;
        for (var index = 0; index < weeks.Length; index++)
        {
            var middle = weeks[index].AddDays(3);
            if (middle.Month == previousMonth) continue;
            var month = middle.ToString("MMM", CultureInfo.GetCultureInfo("en-US"));
            while (labels.Count < index + month.Length) labels.Add(' ');
            for (var offset = 0; offset < month.Length; offset++) labels[index + offset] = month[offset];
            previousMonth = middle.Month;
        }
        var values = stats.Activity.ToDictionary(day => day.Date, day => day.Steps, StringComparer.Ordinal);
        var levels = values.Values.Where(value => value > 0).Distinct().Order().ToArray();
        yield return Style(totalWeeks > maxWeeks ? $"activity · last {maxWeeks} weeks" : "activity", "1;" + palette.Primary);
        yield return "   " + Style(new string(labels.ToArray()).TrimEnd(), "2");
        var weekdays = new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" };
        for (var day = 0; day < 7; day++)
        {
            yield return Style(weekdays[day], "2") + " " + string.Concat(weeks.Select(week =>
            {
                var date = week.AddDays(day);
                if (date < first || date > last) return " ";
                var value = values.GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return Paint(value == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((Array.IndexOf(levels, value) + 1d) / levels.Length * 4)));
            }));
            if (day != 6) yield return "";
        }
        yield return "";
        yield return $"   {Style("less", "2")} {string.Concat(Enumerable.Range(0, 5).Select(Paint))} {Style("more", "2")}";
    }

    private static (string Primary, string[] Activity) Palette()
    {
        var value = Environment.GetEnvironmentVariable("COLORFGBG")?.Split(';')[^1];
        if (value is not null && (value.Trim().Length == 0 || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var background = value.Trim().Length == 0 ? 0 : double.Parse(value, CultureInfo.InvariantCulture);
            if (double.IsFinite(background)) return background >= 7
                ? ("38;2;59;125;216", ["38;2;153;169;192", "38;2;122;155;200", "38;2;90;140;208", "38;2;59;125;216"])
                : ("38;2;250;178;131", ["38;2;117;99;87", "38;2;161;125;102", "38;2;206;152;116", "38;2;250;178;131"]);
        }
        return ("36", ["2;36", "36", "1;36", "1;96"]);
    }
    private static string Row(string name, string value) => "  " + name.PadRight(20) + value;
    private static string Table(string label, string second, string third, string fourth) => Truncate(label, 34).PadRight(34) + second.PadLeft(10) + third.PadLeft(12) + fourth.PadLeft(12);
    private static string Truncate(string value, int width) => value.Length <= width ? value : value[..Math.Clamp(width - 1, 0, value.Length)] + "…";
    private static double Total(TokenUsageInfo tokens) => tokens.Input + tokens.Output + tokens.Reasoning + tokens.Cache.Read + tokens.Cache.Write;
    private static string Number(double value) => value >= 1e9 ? Decimal(value / 1e9) + "b" : value >= 1e6 ? Decimal(value / 1e6) + "m"
        : value >= 1e3 ? Decimal(value / 1e3) + "k" : Math.Floor(value + 0.5).ToString("N0", CultureInfo.GetCultureInfo("en-US"));
    private static string Decimal(double value) { var text = Fixed(value, 1); return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text; }
    private static string Percent(double value) => Fixed(value, value >= 10 ? 1 : 2) + "%";
    private static string Duration(double value) => value < 1000 ? Math.Floor(value + 0.5).ToString(CultureInfo.InvariantCulture) + "ms" : Decimal(value / 1000) + "s";
    private static string Fixed(double value, int places)
    {
        // Number.toFixed rounds the exact binary value, not decimal midpoint-even.
        if (!double.IsFinite(value) || Math.Abs(value) >= 1e21) return value.ToString("G", CultureInfo.InvariantCulture).ToLowerInvariant();
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(value));
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0x000fffffffffffffL);
        if (exponent != 0) mantissa += BigInteger.One << 52;
        var shift = (exponent == 0 ? -1022 : exponent - 1023) - 52;
        var scaled = mantissa * BigInteger.Pow(10, places);
        if (shift >= 0) scaled <<= shift;
        else
        {
            var denominator = BigInteger.One << -shift;
            scaled = BigInteger.DivRem(scaled, denominator, out var remainder);
            if (remainder * 2 >= denominator) scaled++;
        }
        var digits = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(places + 1, '0');
        return (value < 0 ? "-" : "") + digits[..^places] + "." + digits[^places..];
    }
}
