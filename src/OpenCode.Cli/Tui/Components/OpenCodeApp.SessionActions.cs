namespace OpenCode.Cli.Tui.Components;

using System.Collections.Immutable;
using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Schema;

public partial class OpenCodeApp
{
    [Parameter] public Func<SessionId, string, CancellationToken, Task>? RenameSession { get; set; }
    [Parameter] public Func<SessionId, CancellationToken, Task>? DeleteSession { get; set; }
    [Parameter] public Func<IReadOnlyList<SessionInfo>>? ReadSessionCache { get; set; }

    private void MoveTab(SessionTabMove move)
    {
        var before = _tabs.Persisted;
        _tabs = _tabs.Move(move.Key, move.Index);
        QueueTabWrite(before, _tabs.Persisted);
        _dirty = true;
    }

    private void PromoteTab(Guid key) { _tabs = _tabs.Promote(key); _dirty = true; }

    private Task SessionDeleted(SessionId id) => RunConfigurationAction(async token =>
    {
        var before = _tabs.Persisted;
        foreach (var tab in _tabs.Tabs.Where(tab => tab.SessionId == id).ToArray())
        {
            _tabs = _tabs.Close(tab.Key);
        }
        _tabs = _tabs with { Closed = _tabs.Closed.Where(tab => tab.SessionId != id).ToImmutableArray() };
        _deletedTabs.Add(id);
        TrimEditorRoutes();
        if (_sessionId == id) RestoreTab(_tabs.Current, await LoadTab(_tabs.Current, token));
        QueueTabWrite(before, _tabs.Persisted);
        _dirty = true;
    }, throwErrors: true, cancellationToken: CancellationToken.None);

    private string? ResolveDialogCommand(ConsoleKeyInfo key)
    {
        var name = KeyName(key);
        if (name is null || _resolvedBindings is null) return null;
        var stroke = new OpenTui.Blazor.Keymap.KeyStroke(name, key.Modifiers.HasFlag(ConsoleModifiers.Control),
            key.Modifiers.HasFlag(ConsoleModifiers.Shift), key.Modifiers.HasFlag(ConsoleModifiers.Alt));
        return new[] { "session.rename", "session.delete", "session.pin.toggle", "dialog.select.next", "dialog.select.previous",
            "model.dialog.provider", "model.dialog.favorite", "stash.delete", "dialog.integration.rename", "dialog.integration.delete", "dialog.mcp.toggle", "queued_prompt.delete" }
            .FirstOrDefault(id => _resolvedBindings.Get(id).Any(binding => binding.Sequence.Count == 1 && binding.Sequence[0].Stroke == stroke));
    }
}
