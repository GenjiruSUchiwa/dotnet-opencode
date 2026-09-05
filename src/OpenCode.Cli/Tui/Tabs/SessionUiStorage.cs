namespace OpenCode.Cli.Tui.Tabs;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Client-only channel state. Reads and locked mutations never open a server database.</summary>
internal static class SessionUiStorage
{
    private static string Root => Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } root
        ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "opencode", OpenCodeChannel.Name);

    public static async Task<JsonObject> ReadAsync(string name, CancellationToken ct)
    {
        var file = Path.Combine(Root, "tui", name + ".json");
        if (!File.Exists(file)) return new();
        return JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject
            ?? throw new JsonException($"{name} state must be an object; it was not overwritten.");
    }

    public static async Task MutateAsync(string name, Action<JsonObject> change, CancellationToken ct, TimeProvider clock)
    {
        Directory.CreateDirectory(Path.Combine(Root, "tui"));
        Directory.CreateDirectory(Path.Combine(Root, "locks"));
        using var deadline = clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        FileStream? ownership = null;
        while (ownership is null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { ownership = new FileStream(Path.Combine(Root, "locks", name + "-dotnet.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(50), clock, deadline.Token); }
        }
        await using (ownership)
        {
            var document = await ReadAsync(name, deadline.Token);
            change(document);
            var file = Path.Combine(Root, "tui", name + ".json");
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
                File.Move(temporary, file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
