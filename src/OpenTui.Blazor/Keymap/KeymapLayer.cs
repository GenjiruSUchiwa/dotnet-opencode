namespace OpenTui.Blazor.Keymap;

public sealed record KeymapContext
{
    public object? FocusedTarget { get; init; }
    /// <summary>Focused target through root, or just root when no target has focus.</summary>
    public IReadOnlyList<object> FocusPath { get; init; } = [];
    public IReadOnlyDictionary<string, object?> Data { get; init; } = new Dictionary<string, object?>();
}

public sealed record KeymapCondition
{
    public bool Enabled { get; init; } = true;
    public Func<KeymapContext, bool>? When { get; init; }
    public IReadOnlyDictionary<string, object?> Requires { get; init; } = new Dictionary<string, object?>();
    public bool Matches(KeymapContext context) => Enabled && (When?.Invoke(context) ?? true)
        && Requires.All(pair => context.Data.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));
}

public sealed record KeymapInvocation(string? Command, KeymapEvent? Event, KeymapContext Context,
    object? Target = null, string? Input = null);

/// <summary>Return false to reject and try the next command registration. Async work must be admitted by the owner.</summary>
public sealed record KeymapCommand(string Name, Func<KeymapInvocation, bool> Run)
{
    public KeymapCondition Condition { get; init; } = new();
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    /// <summary>Explicit opt-in for command palettes; keybinding defaults are not registrations.</summary>
    public bool Palette { get; init; }
    public Func<KeymapContext, bool>? Suggested { get; init; }
}

public sealed record KeymapBinding(IReadOnlyList<KeySequencePart> Sequence, string? Command = null)
{
    public Func<KeymapInvocation, bool>? Run { get; init; }
    public KeyEventType Event { get; init; }
    public bool PreventDefault { get; init; } = true;
    public bool Fallthrough { get; init; }
    public KeymapCondition Condition { get; init; } = new();
    public string? Description { get; init; }
    public string? Group { get; init; }

    public static IReadOnlyList<KeymapBinding> Compile(string command, BindingSpec spec, KeySequenceParser parser)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var sequences = parser.Parse(spec.Key);
        if (spec.Event == KeyEventType.Release && sequences.Any(sequence => sequence.Count != 1))
            throw new FormatException("Release bindings support exactly one stroke.");
        return sequences.Select(sequence => new KeymapBinding(sequence, command)
        {
            Event = spec.Event, PreventDefault = spec.PreventDefault, Fallthrough = spec.Fallthrough,
            Description = TextAttribute(spec, "desc"), Group = TextAttribute(spec, "group")
        }).ToArray();
    }

    private static string? TextAttribute(BindingSpec spec, string name)
    {
        if (spec.Attributes?.TryGetValue(name, out var value) != true) return null;
        if (value.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Binding '{name}' must be nonempty text.");
        return value.GetString()!.Trim();
    }
}

public enum KeymapTargetMode { FocusWithin, Focus }

public sealed record KeymapLayer
{
    public int Priority { get; init; }
    public object? Target { get; init; }
    public KeymapTargetMode TargetMode { get; init; }
    public KeymapCondition Condition { get; init; } = new();
    public IReadOnlyList<KeymapCommand> Commands { get; init; } = [];
    public IReadOnlyList<KeymapBinding> Bindings { get; init; } = [];
}

/// <summary>Stable registrations; the owner disposes layers when their target is removed or destroyed.</summary>
public sealed class KeymapLayerRegistry
{
    private readonly List<Registration> _layers = [];
    private long _order;

    public IDisposable Register(KeymapLayer layer)
    {
        foreach (var command in layer.Commands) ArgumentException.ThrowIfNullOrWhiteSpace(command.Name, nameof(layer));
        foreach (var binding in layer.Bindings)
        {
            if (binding.Sequence.Count == 0) throw new ArgumentException("A binding requires a nonempty sequence.", nameof(layer));
            if (binding.Event == KeyEventType.Release && binding.Sequence.Count != 1)
                throw new ArgumentException("Release bindings support exactly one stroke.", nameof(layer));
            if (binding.Command is not null) ArgumentException.ThrowIfNullOrWhiteSpace(binding.Command, nameof(layer));
            if (binding.Command is not null && binding.Run is not null) throw new ArgumentException("A binding has either a command ID or an inline handler.", nameof(layer));
        }
        // The source default keymap has no exact/prefix disambiguation addon.
        foreach (var binding in layer.Bindings.Where(binding => binding.Event == KeyEventType.Press && (binding.Command is not null || binding.Run is not null)))
            if (layer.Bindings.Any(other => other.Event == KeyEventType.Press && other.Sequence.Count > binding.Sequence.Count
                && binding.Sequence.Select(part => part.Stroke).SequenceEqual(other.Sequence.Take(binding.Sequence.Count).Select(part => part.Stroke))))
                throw new ArgumentException("An executable binding cannot also prefix another binding in the same layer.", nameof(layer));
        var registration = new Registration(layer with { Commands = layer.Commands.ToArray(), Bindings = layer.Bindings.ToArray() }, ++_order, _layers);
        _layers.Add(registration);
        return registration;
    }

    internal Registration[] Active(KeymapContext context) => _layers.Where(item => item.Layer.Condition.Matches(context)
        && (item.Layer.Target is null || (item.Layer.TargetMode == KeymapTargetMode.Focus
            ? ReferenceEquals(item.Layer.Target, context.FocusedTarget)
            : context.FocusPath.Any(target => ReferenceEquals(target, item.Layer.Target)))))
        .OrderByDescending(item => item.Layer.Priority).ThenByDescending(item => item.Order).ToArray();

    public IReadOnlyList<KeymapCommand> ReachableCommands(KeymapContext context) => Active(context)
        .SelectMany(item => item.Layer.Commands).Where(command => command.Condition.Matches(context))
        .DistinctBy(command => command.Name).ToArray();

    internal sealed class Registration(KeymapLayer layer, long order, List<Registration> owner) : IDisposable
    {
        public KeymapLayer Layer { get; } = layer;
        public long Order { get; } = order;
        public void Dispose() => owner.Remove(this);
    }
}
