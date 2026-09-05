namespace OpenTui.Blazor;

using OpenTui.Blazor.Keymap;

internal static class TextareaBindings
{
    internal static Dictionary<KeyStroke, TextareaBinding> Create()
    {
        var bindings = new Dictionary<KeyStroke, TextareaBinding>();
        foreach (var (name, command) in new[] { ("left", TextareaCommand.Left), ("right", TextareaCommand.Right),
            ("up", TextareaCommand.Up), ("down", TextareaCommand.Down), ("home", TextareaCommand.BufferHome), ("end", TextareaCommand.BufferEnd) })
        {
            Add(name, command); Add(name, command, shift: true, select: true);
        }
        Add("a", TextareaCommand.LineHome, ctrl: true); Add("e", TextareaCommand.LineEnd, ctrl: true);
        Add("a", TextareaCommand.LineHome, ctrl: true, shift: true, select: true); Add("e", TextareaCommand.LineEnd, ctrl: true, shift: true, select: true);
        Add("a", TextareaCommand.VisualHome, meta: true); Add("e", TextareaCommand.VisualEnd, meta: true);
        Add("a", TextareaCommand.VisualHome, meta: true, shift: true, select: true); Add("e", TextareaCommand.VisualEnd, meta: true, shift: true, select: true);
        Add("f", TextareaCommand.Right, ctrl: true); Add("b", TextareaCommand.Left, ctrl: true);
        Add("w", TextareaCommand.DeleteWordLeft, ctrl: true); Add("backspace", TextareaCommand.DeleteWordLeft, ctrl: true);
        Add("d", TextareaCommand.DeleteWordRight, meta: true); Add("delete", TextareaCommand.DeleteWordRight, meta: true);
        Add("delete", TextareaCommand.DeleteWordRight, ctrl: true); Add("d", TextareaCommand.DeleteLine, ctrl: true, shift: true);
        Add("k", TextareaCommand.DeleteToLineEnd, ctrl: true); Add("u", TextareaCommand.DeleteToLineStart, ctrl: true);
        Add("backspace", TextareaCommand.Backspace); Add("backspace", TextareaCommand.Backspace, shift: true);
        Add("d", TextareaCommand.Delete, ctrl: true); Add("delete", TextareaCommand.Delete); Add("delete", TextareaCommand.Delete, shift: true);
        Add("return", TextareaCommand.NewLine); Add("kpenter", TextareaCommand.NewLine); Add("linefeed", TextareaCommand.NewLine);
        Add("return", TextareaCommand.Submit, meta: true); Add("kpenter", TextareaCommand.Submit, meta: true);
        Add("-", TextareaCommand.Undo, ctrl: true); Add(".", TextareaCommand.Redo, ctrl: true);
        Add("z", TextareaCommand.Undo, super: true); Add("z", TextareaCommand.Redo, super: true, shift: true);
        foreach (var (name, command) in new[] { ("f", TextareaCommand.WordRight), ("b", TextareaCommand.WordLeft),
            ("right", TextareaCommand.WordRight), ("left", TextareaCommand.WordLeft) })
        {
            Add(name, command, meta: true); Add(name, command, meta: true, shift: true, select: true);
        }
        Add("right", TextareaCommand.WordRight, ctrl: true); Add("left", TextareaCommand.WordLeft, ctrl: true);
        // User-required aliases target the same commands, not a second word algorithm.
        Add("right", TextareaCommand.WordRight, ctrl: true, shift: true, select: true);
        Add("left", TextareaCommand.WordLeft, ctrl: true, shift: true, select: true);
        Add("backspace", TextareaCommand.DeleteWordLeft, meta: true);
        foreach (var (name, command) in new[] { ("left", TextareaCommand.VisualHome), ("right", TextareaCommand.VisualEnd),
            ("up", TextareaCommand.BufferHome), ("down", TextareaCommand.BufferEnd) })
        {
            Add(name, command, super: true); Add(name, command, super: true, shift: true, select: true);
        }
        Add("a", TextareaCommand.SelectAll, super: true);
        return bindings;

        void Add(string name, TextareaCommand command, bool ctrl = false, bool shift = false, bool meta = false, bool super = false, bool select = false) =>
            bindings[new(name, ctrl, shift, meta, super)] = new(command, select);
    }
}
