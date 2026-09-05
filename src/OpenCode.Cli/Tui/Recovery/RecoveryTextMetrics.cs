namespace OpenCode.Cli.Tui.Recovery;

using OpenTui.Native;

internal sealed class RecoveryTextMetrics : IDisposable
{
    private NativeTextView? _view;
    public int Lines(string text, int width)
    {
        _view ??= new NativeTextView();
        _view.SetText(text);
        _view.SetWrapMode(NativeTextWrapMode.Word);
        return Math.Max(1, (int)_view.Measure(Math.Max(1, width)).LineCount);
    }
    public void Dispose() => _view?.Dispose();
}
