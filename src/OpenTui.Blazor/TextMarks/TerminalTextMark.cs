namespace OpenTui.Blazor.TextMarks;

using OpenTui.Native;

/// <summary>Paint-only range in UTF-16 indexes of Input.Value. End is exclusive.
/// This is NOT a terminal-cell, UTF-8-byte, or native-codepoint range.</summary>
public sealed record TerminalTextMark(int Id, int Start, int End, TerminalTextMarkStyle Style, int Priority = 0);

/// <summary>Resolved styles supplied by the app. No theme or application schema dependency.</summary>
public sealed record TerminalTextMarkStyle(NativeRgba? Foreground = null, NativeRgba? Background = null, uint Attributes = 0);

/// <summary>Managed source-editor interval: display positions, with LF counting as one.
/// Virtual affects editing/navigation; it does not replace the underlying text.</summary>
public sealed record TerminalExtmark(int Id, int Start, int End, bool Virtual = false,
    int TypeId = 0, string? StyleKey = null, int Priority = 0);

public enum TerminalMarkMotion { Set, Left, Right, Up, Down, Direct }
public readonly record struct TerminalMarkDeletion(int Start, int Length);
