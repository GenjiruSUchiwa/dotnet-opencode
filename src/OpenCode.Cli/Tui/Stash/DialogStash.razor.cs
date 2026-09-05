namespace OpenCode.Cli.Tui.Stash;

using System.Globalization;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Cli.Tui.Theme;

/// <summary>Source stash selector. Root restores structured input/marks/mode/history; this component never submits a prompt.</summary>
public partial class DialogStash : ComponentBase, IDisposable
{
    [Inject] public TimeProvider Clock { get; set; } = TimeProvider.System;
    [Parameter, EditorRequired] public PromptStashStore Store { get; set; } = null!;
    [Parameter, EditorRequired] public ThemeTokens Theme { get; set; } = null!;
    [Parameter, EditorRequired] public string Backdrop { get; set; } = null!;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public string DeleteShortcut { get; set; } = "ctrl+d";
    /// <summary>Root verifies current admission uncertainty and whether it can restore every descriptor in this entry.</summary>
    [Parameter, EditorRequired] public Func<StashEntry, PromptStashAccess> CanRestore { get; set; } = null!;
    [Parameter, EditorRequired] public EventCallback<StashEntry> OnRestore { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private PromptStashStore _store = null!;
    private StashSnapshot _snapshot = new(Array.Empty<StashEntry>(), false, false, 0, null);
    private int? _deleting;
    private string? _error;
    private bool _busy;
    private bool _disposed;
    private DialogTheme Colors => new(Theme.Text.Hex, Theme.Subdued.Hex, Theme.Background.Hex, Backdrop,
        Theme.Text.Hex, Theme.ActionBackground(_deleting is null ? ThemeActionVariant.Primary : ThemeActionVariant.Destructive, ThemeActionState.Focused).Hex,
        Theme.ActionText(_deleting is null ? ThemeActionVariant.Primary : ThemeActionVariant.Destructive, ThemeActionState.Focused).Hex, Theme.FormfieldText(ThemeActionState.Selected).Hex,
        Theme.FormfieldBackground(ThemeActionState.Focused).Hex, Theme.FormfieldText(ThemeActionState.Focused).Hex);

    protected override async Task OnInitializedAsync()
    {
        _store = Store;
        _store.Changed += Changed;
        _snapshot = _store.Snapshot;
        try { if (!_snapshot.Loaded) await _store.LoadAsync(); }
        catch (Exception error) { _error = error.Message; }
        _snapshot = _store.Snapshot;
    }
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(Store, _store)) throw new InvalidOperationException("Remount Stash when replacing its root-owned store.");
    }

    private IReadOnlyList<DialogSelectOption<int>> Options => _snapshot.Entries.Select((entry, index) =>
        new DialogSelectOption<int>(index, _deleting == index ? $"Press {DeleteShortcut} again to confirm" : Preview(entry.Prompt.Text),
            RelativeTime(entry.Timestamp, Clock), Footer: entry.Prompt.Text.Count(character => character == '\n') + 1 is > 1 and var lines ? $"~{lines} lines" : null)).Reverse().ToArray();

    private IReadOnlyList<DialogSelectAction<int>> Actions =>
        [new("stash.delete", "delete", DeleteShortcut, option => DeleteAsync(option?.Value))];

    private string? ResolveKey(ConsoleKeyInfo key) => ResolveCommand is not null ? ResolveCommand(key)
        : key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.D ? "stash.delete" : null;

    private void Moved(int value) { _deleting = null; StateHasChanged(); }
    private void Filtered(string value) => _deleting = null;

    private async Task SelectAsync(int index)
    {
        if (_busy || index < 0 || index >= _snapshot.Entries.Count) return;
        if (CanRestore is null || !OnRestore.HasDelegate) { _error = "Structured stash restoration is not connected to the prompt owner."; return; }
        var entry = _snapshot.Entries[index];
        _busy = true;
        _error = null;
        try
        {
            var access = CanRestore(entry);
            access.RequireEditable();
            if (entry.Prompt.HasAdmissionIdentity) throw new InvalidOperationException("This entry contains an admission identity. Reconcile it instead of restoring a new prompt.");
            // Unlike a synchronous JS callback, native root restoration can fail asynchronously.
            // Keep the source entry until the complete structured restore succeeds; never lose it
            // or fall back to text-only restoration on that failure.
            await OnRestore.InvokeAsync(entry);
            var mutation = _store.Take(index, entry, access);
            await mutation.Persistence;
            await OnClose.InvokeAsync();
        }
        catch (Exception error) { _error = error.Message; }
        finally { _busy = false; }
    }

    private async Task DeleteAsync(int? index)
    {
        if (_busy || index is null || index < 0 || index >= _snapshot.Entries.Count) return;
        if (_deleting != index) { _deleting = index; StateHasChanged(); return; }
        _busy = true;
        try
        {
            var mutation = _store.Remove(index.Value, _snapshot.Entries[index.Value]);
            _deleting = null;
            await mutation.Persistence;
        }
        catch (Exception error) { _error = error.Message; }
        finally { _busy = false; if (!_disposed) StateHasChanged(); }
    }

    private void Changed() => _ = RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        await InvokeAsync(() =>
        {
            if (_disposed) return;
            var next = _store.Snapshot;
            if (next.Revision != _snapshot.Revision) _deleting = null;
            _snapshot = next;
            StateHasChanged();
        });
    }

    public static string Preview(string input, int maximumLength = 50)
    {
        var first = input.Split('\n')[0].Trim();
        if (first.Length <= maximumLength) return first;
        var end = Math.Max(0, maximumLength - 1);
        if (end > 0 && char.IsHighSurrogate(first[end - 1])) end--;
        return first[..end] + "…";
    }

    public static string RelativeTime(double timestamp, TimeProvider? clock = null)
    {
        var seconds = Math.Floor(((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() - timestamp) / 1000);
        if (seconds < 60) return "just now";
        var minutes = Math.Floor(seconds / 60);
        if (minutes < 60) return $"{minutes:0}m ago";
        var hours = Math.Floor(minutes / 60);
        if (hours < 24) return $"{hours:0}h ago";
        var days = Math.Floor(hours / 24);
        if (days < 7) return $"{days:0}d ago";
        if (!double.IsFinite(timestamp) || timestamp < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || timestamp > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()) return "Invalid date";
        var date = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp).ToLocalTime();
        return date.ToString("t", CultureInfo.CurrentCulture) + " · " + date.ToString("d", CultureInfo.CurrentCulture);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; if (_store is not null) _store.Changed -= Changed; }
}
