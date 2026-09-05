namespace OpenTui.Blazor;

using OpenTui.Native;

public interface ITextClipboard
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Host-owned adapter; construction does not access the clipboard.</summary>
public sealed class WindowsTextClipboard(IntPtr owner = default) : ITextClipboard
{
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeClipboard.WriteText(text, owner);
        return Task.CompletedTask;
    }
}

public sealed record TerminalSelectedText(string Text, NativeTextSelectionRange? Range, int X, int Y);

/// <summary>Immutable copies of native-selected content from actual scene text views.</summary>
public sealed record TerminalTextSelection(IReadOnlyList<TerminalSelectedText> Parts)
{
    // OpenTUI Selection.getSelectedText merges same-row renderables by x and
    // logical source lines by y. Soft-wrap rows do not create new copied lines.
    public string Text => string.Join('\n', Parts.OrderBy(part => part.Y).ThenBy(part => part.X)
        .SelectMany(part => part.Text.Split('\n').Select((line, index) => (Text: line, X: part.X, Y: part.Y + index)))
        .GroupBy(line => line.Y).OrderBy(group => group.Key)
        .Select(group => string.Concat(group.OrderBy(line => line.X).Select(line => line.Text))));
    public bool HasText => Parts.Any(part => part.Text.Length > 0);
}
