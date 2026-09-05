using OpenTui.Blazor.Keymap;

namespace OpenCode.Cli.Tui.Keymap;

public sealed record TuiKeymapCommand(KeymapCommand Command)
{
    public bool Bind { get; init; } = true;
    public string? FallbackBinding { get; init; }
    public bool Palette { get; init; }
    public string? SlashName { get; init; }
    public IReadOnlyList<string> SlashAliases { get; init; } = [];
}

/// <summary>Named OpenCode command adapter. Defaults describe bindings; only registered callbacks execute.</summary>
public static class TuiKeymapLayer
{
    public const string ModeKey = "opencode.mode";
    public const string BaseMode = "base";
    public const string ModalMode = "modal";
    public const string GlobalMode = "global";
    public const string CommandPaletteCommand = "command.palette.show";

    public static KeymapLayer Create(TuiKeybindConfig config, IReadOnlyList<TuiKeymapCommand> commands,
        string mode = BaseMode, int priority = 0, KeymapCondition? condition = null,
        object? target = null, KeymapTargetMode targetMode = KeymapTargetMode.FocusWithin,
        IReadOnlyList<string>? bindings = null)
    {
        var current = condition ?? new KeymapCondition();
        return new()
        {
            Priority = priority, Target = target, TargetMode = targetMode,
            Condition = mode == GlobalMode ? current : current with
            {
                Requires = new Dictionary<string, object?>(current.Requires) { [ModeKey] = mode }
            },
            Commands = commands.Select(command => command.Command).ToArray(),
            Bindings = commands.SelectMany(command =>
            {
                if (!command.Bind) return Array.Empty<KeymapBinding>();
                var configured = config.Get(command.Command.Name);
                // context/keymap.tsx falls back when lookup is empty, including explicit false/none.
                if (configured.Count > 0 || command.FallbackBinding is null) return configured;
                return KeymapBinding.Compile(command.Command.Name, new(new BindingKey.Text(command.FallbackBinding)), config.Parser);
            }).Concat((bindings ?? []).SelectMany(config.Get)).ToArray()
        };
    }
}

/// <summary>Mutually exclusive modes with independently disposable pushes, matching createMode.</summary>
public sealed class TuiKeymapMode
{
    private readonly List<Lease> _stack = [];
    public string Current => _stack.LastOrDefault()?.Mode ?? TuiKeymapLayer.BaseMode;
    public IDisposable Push(string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        var lease = new Lease(mode, _stack);
        _stack.Add(lease);
        return lease;
    }

    public KeymapContext Apply(KeymapContext context) => context with
    {
        Data = new Dictionary<string, object?>(context.Data) { [TuiKeymapLayer.ModeKey] = Current }
    };

    private sealed class Lease(string mode, List<Lease> stack) : IDisposable
    {
        public string Mode { get; } = mode;
        public void Dispose() => stack.Remove(this);
    }
}
