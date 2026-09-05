namespace OpenCode.Cli.Tui;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Cli.Tui.Keymap;

internal static class ClientKeybindSettings
{
    internal static async Task<TuiKeybindConfig> LoadAsync(CancellationToken cancellationToken)
    {
        var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var file = Path.Combine(root, "opencode", "cli.json");
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var config = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken), documentOptions: options) as JsonObject
            ?? throw new JsonException("CLI configuration must be an object.") : new JsonObject();
        if (!config.ContainsKey("keybinds")) config["keybinds"] = new JsonObject();
        // Keep the already-shipped native Ctrl+O session-picker alias unless the
        // user explicitly configures this command. No file is rewritten here.
        if (config["keybinds"] is JsonObject bindings && !bindings.ContainsKey("session.list")) bindings["session.list"] = "<leader>l,ctrl+o";
        using var document = JsonDocument.Parse(config.ToJsonString());
        return TuiKeybindConfig.FromCliConfig(document.RootElement);
    }
}
