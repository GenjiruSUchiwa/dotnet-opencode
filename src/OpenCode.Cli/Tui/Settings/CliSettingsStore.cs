namespace OpenCode.Cli.Tui.Settings;

using System.Text;
using System.Text.Json;

/// <summary>Shared global CLI preferences. Construction and reading never migrate, copy, or create files.</summary>
public sealed class CliSettingsStore
{
    public const string SchemaUrl = "https://opencode.ai/v2/cli.json";
    private readonly SemaphoreSlim _writes = new(1, 1);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string FilePath { get; }

    public CliSettingsStore(string? filePath = null)
    {
        var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var directory = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR") is { Length: > 0 } configDirectory
            ? configDirectory : Path.Combine(root, "opencode");
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(directory, "cli.json"));
    }

    public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken = default) =>
        JsoncSettingsEditor.Parse(await ReadText(cancellationToken));

    public Task<JsonElement> SetAsync(string id, JsonElement? value, CancellationToken cancellationToken = default) =>
        SetManyAsync([new(id, value)], cancellationToken);

    public async Task<JsonElement> SetManyAsync(IReadOnlyList<CliSettingEdit> changes, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            // Read the latest file for every action, rather than writing a stale
            // in-memory settings snapshot over another client's unrelated edits.
            var text = await ReadText(cancellationToken);
            var next = changes.Aggregate(text, (current, edit) => JsoncSettingsEditor.Set(current, edit.Id.Split('.'), edit.Value));
            if (next == text) return JsoncSettingsEditor.Parse(text);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = new FileStream(temporary, options))
                {
                    await output.WriteAsync(Utf8.GetBytes(next.EndsWith('\n') ? next : next + "\n"), cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (await ReadText(cancellationToken) != text) throw new IOException("CLI configuration changed while saving. Retry the setting change; the external edit was preserved.");
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return JsoncSettingsEditor.Parse(next);
        }
        finally { _writes.Release(); }
    }

    private async Task<string> ReadText(CancellationToken cancellationToken)
    {
        try { return Utf8.GetString(await File.ReadAllBytesAsync(FilePath, cancellationToken)); }
        catch (FileNotFoundException) { return "{\n  \"$schema\": \"" + SchemaUrl + "\"\n}\n"; }
        catch (DirectoryNotFoundException) { return "{\n  \"$schema\": \"" + SchemaUrl + "\"\n}\n"; }
    }
}

public sealed record CliSettingEdit(string Id, JsonElement? Value);
