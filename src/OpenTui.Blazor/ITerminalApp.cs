namespace OpenTui.Blazor;

/// <summary>
/// Input and lifecycle boundary for a root Blazor terminal component. The host
/// invokes every member on its renderer dispatcher; implementations own app state.
/// </summary>
public interface ITerminalApp
{
    bool ExitRequested { get; }
    void Resize(int width, int height);
    void HandleKey(ConsoleKeyInfo key);
    void Paste(string text);
    void OnInputError(string message) { }
    Keymap.KeymapDispatchResult? DispatchKeymap(ConsoleKeyInfo key, Keymap.KeymapContext context, TimeSpan now) => null;
    /// <summary>Override to feed TryGetKeymapEvent directly into the app's real dispatcher. The default only adapts lossless legacy presses.</summary>
    Keymap.KeymapDispatchResult? DispatchKeymap(TerminalKeyInput key, Keymap.KeymapContext context, TimeSpan now)
    {
        var projection = key.ProjectConsoleKeys();
        return projection.IsExact && projection.Keys.Count == 1 ? DispatchKeymap(projection.Keys[0], context, now) : null;
    }
    void TickKeymap(TimeSpan now) { }
    /// <summary>Coalesce application state changes before the next terminal frame.</summary>
    void OnFrame() { }
    void OnTerminalFocusChanged(bool focused) { }
    void OnCapabilitiesChanged(Native.NativeTerminalCapabilities capabilities) { }
    Task StopAsync();
}
