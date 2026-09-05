namespace OpenTui.Blazor;

public abstract class TerminalRoutedEventArgs(CancellationToken cancellationToken) : EventArgs
{
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public bool DefaultPrevented { get; private set; }
    public bool PropagationStopped { get; private set; }
    public void PreventDefault() => DefaultPrevented = true;
    public void StopPropagation() => PropagationStopped = true;
}
public sealed class TerminalRoutedKeyEventArgs(TerminalKeyInput input, CancellationToken cancellationToken = default)
    : TerminalRoutedEventArgs(cancellationToken)
{
    public TerminalKeyInput Input { get; } = input;
}
public sealed class TerminalRoutedPasteEventArgs(string text, CancellationToken cancellationToken = default)
    : TerminalRoutedEventArgs(cancellationToken)
{
    public string Text { get; } = text;
}
