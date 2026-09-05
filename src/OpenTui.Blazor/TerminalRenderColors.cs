namespace OpenTui.Blazor;

using OpenTui.Native;

/// <summary>Host-resolved colors. No application theme lookup occurs in the renderer.</summary>
/// <remarks>Null selection overrides preserve native per-run inversion. Form-control
/// selected-state colors are not defaults for text selection. View-local overrides
/// take precedence over host overrides; transparent text defaults remain transparent.</remarks>
public sealed record TerminalRenderColors(NativeRgba Foreground, NativeRgba Background,
    NativeRgba? Cursor = null, NativeRgba? SelectionForeground = null, NativeRgba? SelectionBackground = null)
{
    public static TerminalRenderColors Default { get; } = new(NativeRgba.White, new NativeRgba(10, 10, 10));
}
