namespace OpenTui.Native;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>Native highlight ABI: u32 start/end/style, u8 priority, aligned u16 reference; 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct NativeTextHighlight(uint start, uint end, uint styleId, byte priority = 0, ushort reference = 0)
{
    public readonly uint Start = start;
    public readonly uint End = end;
    public readonly uint StyleId = styleId;
    public readonly byte Priority = priority;
    public readonly ushort Reference = reference;
}

public static unsafe partial class OpenTuiNative
{
    [LibraryImport(LibName, EntryPoint = "syntaxStyleRegister")]
    private static partial uint RegisterSyntaxCore(uint style, byte* name, uint length, NativeRgba* foreground, NativeRgba* background, uint attributes);
    [LibraryImport(LibName, EntryPoint = "syntaxStyleResolveByName")]
    private static partial uint ResolveSyntaxCore(uint style, byte* name, uint length);
    [LibraryImport(LibName, EntryPoint = "syntaxStyleGetStyleCount")]
    internal static partial uint SyntaxStyleCount(uint style);
    [LibraryImport(LibName, EntryPoint = "textBufferAddHighlightByCharRange")]
    internal static partial void AddTextDisplayHighlight(uint buffer, in NativeTextHighlight highlight);
    [LibraryImport(LibName, EntryPoint = "textBufferAddHighlight")]
    internal static partial void AddTextLineHighlight(uint buffer, uint line, in NativeTextHighlight highlight);
    [LibraryImport(LibName, EntryPoint = "textBufferRemoveHighlightsByRef")]
    internal static partial void RemoveTextHighlights(uint buffer, ushort reference);
    [LibraryImport(LibName, EntryPoint = "textBufferClearLineHighlights")]
    internal static partial void ClearTextLineHighlights(uint buffer, uint line);
    [LibraryImport(LibName, EntryPoint = "textBufferGetHighlightCount")]
    internal static partial uint TextHighlightCount(uint buffer);

    internal static uint RegisterSyntax(uint style, NativeSyntaxRule rule)
    {
        var name = Encoding.UTF8.GetBytes(rule.Scope);
        var fg = rule.Foreground.GetValueOrDefault();
        var bg = rule.Background.GetValueOrDefault();
        fixed (byte* bytes = name) return RegisterSyntaxCore(style, bytes, (uint)name.Length,
            rule.Foreground.HasValue ? &fg : null, rule.Background.HasValue ? &bg : null, rule.Attributes);
    }
    internal static uint ResolveSyntax(uint style, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        fixed (byte* data = bytes) return ResolveSyntaxCore(style, data, (uint)bytes.Length);
    }
    internal static void ClearTextHighlights(uint buffer) => TextBufferClearAllHighlights(buffer);
}
