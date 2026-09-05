namespace OpenCode.Cli.Tui.Theme;

using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record CliThemeSettings(string Name = "opencode", ThemeModePreference Mode = ThemeModePreference.System)
{
    public static CliThemeSettings Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return FromConfig(document.RootElement);
    }

    public static CliThemeSettings FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new FormatException("CLI configuration must be an object.");
        if (!config.TryGetProperty("theme", out var theme)) return new();
        if (theme.ValueKind != JsonValueKind.Object) throw new NotSupportedException("Legacy CLI theme settings must be migrated to theme.name and theme.mode.");
        var name = theme.TryGetProperty("name", out var selected) ? selected.ValueKind == JsonValueKind.String ? selected.GetString()!
            : throw new FormatException("theme.name must be a string.") : "opencode";
        var mode = theme.TryGetProperty("mode", out var preferred) ? preferred.ValueKind == JsonValueKind.String ? preferred.GetString() switch
        {
            "light" => ThemeModePreference.Light, "dark" => ThemeModePreference.Dark, "system" => ThemeModePreference.System,
            _ => throw new FormatException("theme.mode must be light, dark, or system.")
        } : throw new FormatException("theme.mode must be a string.") : ThemeModePreference.System;
        return new(name, mode);
    }

    public static string GlobalConfigDirectory() => Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } configured
        ? configured : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } root ? root
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "opencode"));

    /// <summary>Reads only shared global cli.json. No project CLI override or migration writes.</summary>
    public static async Task<CliThemeSettings> ReadAsync(string? configDirectory = null, CancellationToken ct = default)
    {
        string text;
        try { text = await File.ReadAllTextAsync(Path.Combine(configDirectory ?? GlobalConfigDirectory(), "cli.json"), ct); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        return Parse(text);
    }

    /// <summary>Returns a modified copy for the root's shared config writer. Does not write files.</summary>
    public JsonObject ApplyTo(JsonObject config)
    {
        var result = config.DeepClone().AsObject();
        result["theme"] ??= new JsonObject();
        result["theme"]!["name"] = Name;
        result["theme"]!["mode"] = Mode.ToString().ToLowerInvariant();
        return result;
    }
}

public static class ThemeDiscovery
{
    public static IReadOnlyList<string> ConfigDirectories(string globalConfig, string cwd)
    {
        var ancestors = new List<string>();
        for (var directory = new DirectoryInfo(Path.GetFullPath(cwd)); directory is not null; directory = directory.Parent)
            ancestors.Add(Path.Combine(directory.FullName, ".opencode"));
        ancestors.Reverse();
        return new[] { globalConfig }.Concat(ancestors).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static async Task<IReadOnlyDictionary<string, JsonObject>> DiscoverAsync(IEnumerable<string> directories, CancellationToken ct = default)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(Path.Combine(directory, "themes")).GetFileSystemInfos(); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var entry in entries.Where(entry => (entry.Attributes & FileAttributes.Directory) == FileAttributes.None || (entry.Attributes & FileAttributes.ReparsePoint) != FileAttributes.None)
                .Where(entry => Path.GetExtension(entry.Name) == ".json").OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                var source = JsonNode.Parse(await File.ReadAllTextAsync(entry.FullName, ct));
                if (source is JsonObject document && (document.ContainsKey("version") || document.ContainsKey("theme")))
                    result[Path.GetFileNameWithoutExtension(entry.Name)] = document;
            }
        }
        return result;
    }
}
