namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>A managed snapshot; no fields borrow native memory.</summary>
public sealed record NativeTerminalCapabilities
{
    public bool KittyKeyboard { get; init; }
    public bool KittyGraphics { get; init; }
    public bool Rgb { get; init; }
    public bool Ansi256 { get; init; }
    /// <summary>0=wcwidth, 1=unicode, 3=unicode-wide.</summary>
    public byte WidthMethod { get; init; }
    public bool SgrPixels { get; init; }
    public bool ColorSchemeUpdates { get; init; }
    public bool ExplicitWidth { get; init; }
    public bool ScaledText { get; init; }
    public bool Sixel { get; init; }
    public bool FocusTracking { get; init; }
    public bool Sync { get; init; }
    public bool BracketedPaste { get; init; }
    public bool Hyperlinks { get; init; }
    public bool Osc52 { get; init; }
    public bool Notifications { get; init; }
    public bool ExplicitCursorPositioning { get; init; }
    public bool Remote { get; init; }
    /// <summary>0=none, 1=tmux, 2=zellij, 3=screen, 4=unknown.</summary>
    public byte Multiplexer { get; init; }
    /// <summary>0=auto, 1=kitty, 2=sixel, 3=blocks.</summary>
    public byte ImageProtocol { get; init; }
    public string TerminalName { get; init; } = "";
    public string TerminalVersion { get; init; } = "";
    public bool TerminalFromXtversion { get; init; }
    /// <summary>0=unknown, 1=supported, 2=unsupported.</summary>
    public byte Osc52Support { get; init; }
}

public static unsafe partial class OpenTuiNative
{
    // Upstream zig-structs: 20 byte fields, four pointer/u64 fields, two byte fields.
    // Natural x64 alignment: name@24, nameLength@32, version@40, versionLength@48;
    // fromXtversion@56, osc52Support@57; size 64. Do not pack this structure.
    [StructLayout(LayoutKind.Sequential)]
    private struct TerminalCapabilitiesData
    {
        public byte KittyKeyboard;
        public byte KittyGraphics;
        public byte Rgb;
        public byte Ansi256;
        public byte WidthMethod;
        public byte SgrPixels;
        public byte ColorSchemeUpdates;
        public byte ExplicitWidth;
        public byte ScaledText;
        public byte Sixel;
        public byte FocusTracking;
        public byte Sync;
        public byte BracketedPaste;
        public byte Hyperlinks;
        public byte Osc52;
        public byte Notifications;
        public byte ExplicitCursorPositioning;
        public byte Remote;
        public byte Multiplexer;
        public byte ImageProtocol;
        public byte* TerminalName;
        public ulong TerminalNameLength;
        public byte* TerminalVersion;
        public ulong TerminalVersionLength;
        public byte TerminalFromXtversion;
        public byte Osc52Support;
    }

    [LibraryImport(LibName, EntryPoint = "getTerminalCapabilities")]
    private static partial void GetTerminalCapabilities(uint renderer, out TerminalCapabilitiesData capabilities);

    /// <summary>Copies capabilities and native-owned UTF-8 names into a managed snapshot.</summary>
    /// <remarks>Serialize with renderer mutation and disposal. Call after negotiation changes, not per frame.</remarks>
    public static NativeTerminalCapabilities GetTerminalCapabilities(uint renderer)
    {
        ArgumentOutOfRangeException.ThrowIfZero(renderer);
        GetTerminalCapabilities(renderer, out var data);
        return new NativeTerminalCapabilities
        {
            KittyKeyboard = data.KittyKeyboard != 0,
            KittyGraphics = data.KittyGraphics != 0,
            Rgb = data.Rgb != 0,
            Ansi256 = data.Ansi256 != 0,
            WidthMethod = data.WidthMethod,
            SgrPixels = data.SgrPixels != 0,
            ColorSchemeUpdates = data.ColorSchemeUpdates != 0,
            ExplicitWidth = data.ExplicitWidth != 0,
            ScaledText = data.ScaledText != 0,
            Sixel = data.Sixel != 0,
            FocusTracking = data.FocusTracking != 0,
            Sync = data.Sync != 0,
            BracketedPaste = data.BracketedPaste != 0,
            Hyperlinks = data.Hyperlinks != 0,
            Osc52 = data.Osc52 != 0,
            Notifications = data.Notifications != 0,
            ExplicitCursorPositioning = data.ExplicitCursorPositioning != 0,
            Remote = data.Remote != 0,
            Multiplexer = data.Multiplexer,
            ImageProtocol = data.ImageProtocol,
            TerminalName = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data.TerminalName, checked((int)data.TerminalNameLength))),
            TerminalVersion = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(data.TerminalVersion, checked((int)data.TerminalVersionLength))),
            TerminalFromXtversion = data.TerminalFromXtversion != 0,
            Osc52Support = data.Osc52Support
        };
    }
}
