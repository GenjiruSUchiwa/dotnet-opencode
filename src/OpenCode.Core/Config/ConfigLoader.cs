namespace OpenCode.Core.Config;

using System.Text.Json;
using OpenCode.Schema;

public sealed class ConfigLoader
{
    public static string GetDefaultConfigDirectory()
    {
        var env = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "opencode");
    }

    public static string GetDefaultDataDirectory()
    {
        var env = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrEmpty(env)) return env;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "opencode");
    }

    public static OpenCodeConfig LoadConfig(string? configPath = null)
    {
        configPath ??= Path.Combine(GetDefaultConfigDirectory(), "opencode.json");
        if (!File.Exists(configPath))
        {
            return new OpenCodeConfig();
        }

        var json = File.ReadAllText(configPath);
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        return JsonSerializer.Deserialize<OpenCodeConfig>(json, options) ?? new OpenCodeConfig();
    }

    public static Dictionary<string, AuthEntry> LoadAuth(string? authPath = null)
    {
        authPath ??= Path.Combine(GetDefaultDataDirectory(), "auth.json");
        if (!File.Exists(authPath))
        {
            return [];
        }

        var json = File.ReadAllText(authPath);
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        return JsonSerializer.Deserialize<Dictionary<string, AuthEntry>>(json, options) ?? [];
    }
}
