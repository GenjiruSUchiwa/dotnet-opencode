namespace OpenTui.Blazor.Components;

using Microsoft.AspNetCore.Components;

public sealed record DialogEntry(RenderFragment Content, ModalSize Size = ModalSize.Medium,
    bool Centered = false, Action? OnClose = null, object? Key = null);

/// <summary>Dispatcher-owned dialog navigation. Removed entries receive OnClose exactly once.</summary>
public sealed class DialogStack
{
    private readonly List<DialogEntry> _entries = [];
    public IReadOnlyList<DialogEntry> Entries => _entries.AsReadOnly();
    public DialogEntry? Current => _entries.LastOrDefault();
    public event Action? Changed;

    public void Push(DialogEntry entry)
    {
        _entries.Add(entry);
        Changed?.Invoke();
    }
    public void Replace(DialogEntry entry)
    {
        var previous = _entries.ToArray();
        _entries.Clear();
        _entries.Add(entry);
        foreach (var item in previous) item.OnClose?.Invoke();
        Changed?.Invoke();
    }
    public void Pop()
    {
        if (Current is not { } entry) return;
        _entries.RemoveAt(_entries.Count - 1);
        entry.OnClose?.Invoke();
        Changed?.Invoke();
    }
    public void Clear()
    {
        var previous = _entries.ToArray();
        _entries.Clear();
        foreach (var entry in previous) entry.OnClose?.Invoke();
        Changed?.Invoke();
    }
}
