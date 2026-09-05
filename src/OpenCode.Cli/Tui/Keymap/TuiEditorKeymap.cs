using OpenTui.Blazor.Keymap;

namespace OpenCode.Cli.Tui.Keymap;

/// <summary>Managed textarea bindings. Editing remains in the app's existing editor callbacks.</summary>
public static class TuiEditorKeymap
{
    // @opentui/core defaultTextareaKeyBindings, in source order; OpenCode config is prepended.
    private static readonly (string Key, string Command)[] NativeDefaults =
    [
        ("left", "input.move.left"), ("right", "input.move.right"), ("up", "input.move.up"), ("down", "input.move.down"),
        ("shift+left", "input.select.left"), ("shift+right", "input.select.right"), ("shift+up", "input.select.up"), ("shift+down", "input.select.down"),
        ("home", "input.buffer.home"), ("end", "input.buffer.end"), ("shift+home", "input.select.buffer.home"), ("shift+end", "input.select.buffer.end"),
        ("ctrl+a", "input.line.home"), ("ctrl+e", "input.line.end"), ("ctrl+shift+a", "input.select.line.home"), ("ctrl+shift+e", "input.select.line.end"),
        ("meta+a", "input.visual.line.home"), ("meta+e", "input.visual.line.end"), ("meta+shift+a", "input.select.visual.line.home"), ("meta+shift+e", "input.select.visual.line.end"),
        ("ctrl+f", "input.move.right"), ("ctrl+b", "input.move.left"), ("ctrl+w", "input.delete.word.backward"), ("ctrl+backspace", "input.delete.word.backward"),
        ("meta+d", "input.delete.word.forward"), ("meta+delete", "input.delete.word.forward"), ("ctrl+delete", "input.delete.word.forward"),
        ("ctrl+shift+d", "input.delete.line"), ("ctrl+k", "input.delete.to.line.end"), ("ctrl+u", "input.delete.to.line.start"),
        ("backspace", "input.backspace"), ("shift+backspace", "input.backspace"), ("ctrl+d", "input.delete"), ("delete", "input.delete"), ("shift+delete", "input.delete"),
        ("return", "input.newline"), ("kpenter", "input.newline"), ("linefeed", "input.newline"), ("meta+return", "input.submit"), ("meta+kpenter", "input.submit"),
        ("ctrl+-", "input.undo"), ("ctrl+.", "input.redo"), ("super+z", "input.undo"), ("super+shift+z", "input.redo"),
        ("meta+f", "input.word.forward"), ("meta+b", "input.word.backward"), ("meta+right", "input.word.forward"), ("meta+left", "input.word.backward"),
        ("ctrl+right", "input.word.forward"), ("ctrl+left", "input.word.backward"),
        ("meta+shift+f", "input.select.word.forward"), ("meta+shift+b", "input.select.word.backward"),
        ("meta+shift+right", "input.select.word.forward"), ("meta+shift+left", "input.select.word.backward"),
        ("meta+backspace", "input.delete.word.backward"), ("super+left", "input.visual.line.home"), ("super+right", "input.visual.line.end"),
        ("super+up", "input.buffer.home"), ("super+down", "input.buffer.end"),
        ("super+shift+left", "input.select.visual.line.home"), ("super+shift+right", "input.select.visual.line.end"),
        ("super+shift+up", "input.select.buffer.home"), ("super+shift+down", "input.select.buffer.end"), ("super+a", "input.select.all")
    ];

    // context/keymap.tsx managed textarea lookup order, not the native defaults' action order.
    public static IReadOnlyList<string> CommandIds { get; } = Array.AsReadOnly<string>([
        "input.move.left", "input.move.right", "input.move.up", "input.move.down",
        "input.select.left", "input.select.right", "input.select.up", "input.select.down",
        "input.line.home", "input.line.end", "input.select.line.home", "input.select.line.end",
        "input.visual.line.home", "input.visual.line.end", "input.select.visual.line.home", "input.select.visual.line.end",
        "input.buffer.home", "input.buffer.end", "input.select.buffer.home", "input.select.buffer.end",
        "input.delete.line", "input.delete.to.line.end", "input.delete.to.line.start", "input.backspace", "input.delete",
        "input.newline", "input.undo", "input.redo", "input.word.forward", "input.word.backward",
        "input.select.word.forward", "input.select.word.backward", "input.delete.word.forward", "input.delete.word.backward",
        "input.select.all", "input.submit"
    ]);

    /// <param name="commands">Actual supported editor callbacks. Unimplemented commands must not be registered as successful no-ops.</param>
    /// <param name="focusedEditor">True for any live focused editor, including a single-line Input.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The diagnostic lists the actual unsupported command IDs; preserve its existing text.")]
    public static KeymapLayer CreateCommands(IReadOnlyList<KeymapCommand> commands, Func<KeymapContext, bool> focusedEditor)
    {
        var unknown = commands.Where(command => !CommandIds.Contains(command.Name)).Select(command => command.Name).ToArray();
        if (unknown.Length > 0) throw new ArgumentException($"Unknown editor commands: {string.Join(", ", unknown)}");
        return new() { Condition = new() { When = focusedEditor }, Commands = commands };
    }

    /// <param name="focusedTextarea">True only for a live, focused multiline textarea, not a single-line Input.</param>
    public static KeymapLayer CreateBindings(TuiKeybindConfig config, Func<KeymapContext, bool> focusedTextarea)
    {
        return new()
        {
            Condition = new() { When = focusedTextarea },
            Bindings = CommandIds.SelectMany(config.Get).Concat(NativeDefaults.SelectMany(item =>
                KeymapBinding.Compile(item.Command, new(new BindingKey.Text(item.Key)), config.Parser))).ToArray()
        };
    }
}
