namespace OpenCode.Cli.Tui.Stash;

using System.Text.Json;

public static class PromptStashCodec
{
    public const int MaximumEntries = 50;

    public static IReadOnlyList<StashEntry> Parse(string text)
    {
        var result = new List<StashEntry>();
        foreach (var line in text.Split('\n').Where(line => line.Length > 0))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var entry = document.RootElement;
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("prompt", out var prompt)
                    || StashPrompt.ParsePromptInfo(prompt) is not { } parsed || !entry.TryGetProperty("timestamp", out var timestamp)
                    || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetDouble(out var time) || !double.IsFinite(time)) continue;
                result.Add(new(parsed, time));
            }
            catch (JsonException) { }
        }
        return Array.AsReadOnly(result.TakeLast(MaximumEntries).ToArray());
    }

    /// <summary>Source-compatible history parsing for root-owned history import, without adding a second history file/service.</summary>
    public static IReadOnlyList<StashPrompt> ParseHistory(string text)
    {
        var result = new List<StashPrompt>();
        foreach (var line in text.Split('\n').Where(line => line.Length > 0))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (StashPrompt.ParsePromptInfo(document.RootElement) is { } prompt) result.Add(prompt);
            }
            catch (JsonException) { }
        }
        return Array.AsReadOnly(result.TakeLast(MaximumEntries).ToArray());
    }

    public static string Encode(StashEntry entry) => JsonSerializer.Serialize(new StashDiskEntry(entry.Prompt.Value, entry.Timestamp), StashJsonContext.Default.StashDiskEntry);
    public static string EncodeLines(IEnumerable<StashEntry> entries) => string.Concat(entries.Select(entry => Encode(entry) + "\n"));
}
