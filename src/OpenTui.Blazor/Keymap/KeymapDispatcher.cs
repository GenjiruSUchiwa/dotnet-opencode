using System.Text;

namespace OpenTui.Blazor.Keymap;

public enum KeymapDispatchReason { NoMatch, Pending, Handled, Rejected, SequenceMiss, SequenceCleared }

public sealed record KeymapDispatchResult(bool Handled, bool PreventDefault, bool StopPropagation,
    IReadOnlyList<KeySequencePart> Pending, IReadOnlyList<KeymapInvocation> Actions, KeymapDispatchReason Reason)
{
    public KeymapInvocation? Action => Actions.FirstOrDefault();
}

public sealed record KeymapDispatchOptions
{
    public KeyStroke? TimedLeader { get; init; }
    public TimeSpan LeaderTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);
    public bool EscapeClearsPending { get; init; }
    public bool BackspacePopsPending { get; init; }
    public bool BaseLayoutFallback { get; init; }
}

/// <summary>Pure host-driven dispatch. Serialize calls on the UI owner; no timers, input loop or application callbacks are installed.</summary>
public sealed class KeymapDispatcher(KeymapDispatchOptions? options = null)
{
    private KeymapDispatchOptions _options = options ?? new();
    private Capture[] _captures = [];
    private int _depth;
    public TimeSpan? Deadline { get; private set; }
    public IReadOnlyList<KeySequencePart> Pending => _captures.Length == 0 ? [] : _captures[0].Binding.Sequence.Take(_depth).ToArray();
    public TimeSpan LeaderTimeout => _options.LeaderTimeout;

    public void SetLeaderTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _options = _options with { LeaderTimeout = timeout };
        ClearPending();
    }

    public void ClearPending()
    {
        _captures = [];
        _depth = 0;
        Deadline = null;
    }

    /// <summary>Call with monotonic elapsed time, including from the owner's render tick, to expire leader UI state.</summary>
    public bool Expire(TimeSpan now)
    {
        if (Deadline is not { } deadline || now < deadline) return false;
        ClearPending();
        return true;
    }

    public KeymapDispatchResult Dispatch(KeymapEvent input, KeymapLayerRegistry registry, KeymapContext context, TimeSpan now)
    {
        Expire(now);
        var layers = registry.Active(context);
        _captures = _captures.Where(capture => layers.Contains(capture.Layer) && Reachable(capture, layers, context)).ToArray();
        if (_captures.Length == 0) ClearPending();
        if (input.PropagationStopped) return Result(false, false, [], KeymapDispatchReason.NoMatch);
        if (input.Type == KeyEventType.Press && _captures.Length > 0)
        {
            if (_options.EscapeClearsPending && input.Stroke.Name == "escape")
            {
                ClearPending();
                return Result(true, true, [], KeymapDispatchReason.SequenceCleared);
            }
            if (_options.BackspacePopsPending && input.Stroke.Name == "backspace")
            {
                if (--_depth == 0) ClearPending();
                else Arm(now);
                return Result(true, true, [], _depth == 0 ? KeymapDispatchReason.SequenceCleared : KeymapDispatchReason.Pending);
            }
        }
        var matches = new List<KeyStroke> { input.Stroke };
        if (_options.BaseLayoutFallback && input.BaseCode is >= 32 and not 127 && Rune.IsValid(input.BaseCode.Value))
        {
            var name = new Rune(input.BaseCode.Value).ToString();
            if (name.Length == 1 && name[0] is >= 'A' and <= 'Z') name = name.ToLowerInvariant();
            var fallback = new KeyStroke(name, input.Stroke.Ctrl, input.Stroke.Shift, input.Stroke.Meta, input.Stroke.Super, input.Stroke.Hyper);
            if (!matches.Contains(fallback)) matches.Add(fallback);
        }
        var actions = new List<KeymapInvocation>();
        var prevent = false;
        var rejected = false;
        var pending = input.Type == KeyEventType.Press && _captures.Length > 0;
        foreach (var match in matches)
        {
            var captures = pending
                ? _captures.Where(capture => capture.Binding.Sequence[_depth].Stroke == match).ToArray()
                : layers.SelectMany(layer => layer.Layer.Bindings.Where(binding => binding.Event == input.Type && binding.Sequence[0].Stroke == match)
                    .Select(binding => new Capture(layer, binding))).Where(capture => Reachable(capture, layers, context)).ToArray();
            if (captures.Length == 0) continue;
            var nextDepth = pending ? _depth + 1 : 1;
            foreach (var group in captures.GroupBy(capture => capture.Layer))
            {
                var continuations = group.Where(capture => capture.Binding.Sequence.Count > nextDepth).ToArray();
                if (input.Type == KeyEventType.Press && continuations.Length > 0 && (!pending || actions.Count == 0))
                {
                    _captures = captures.SkipWhile(capture => capture.Layer != group.Key).Where(capture => capture.Binding.Sequence.Count > nextDepth).ToArray();
                    _depth = nextDepth;
                    Arm(now);
                    return Result(true, true, actions, KeymapDispatchReason.Pending);
                }
                foreach (var capture in group.Where(capture => capture.Binding.Sequence.Count == nextDepth))
                {
                    var invocation = Run(capture, input, layers, context);
                    if (invocation is null) { rejected = true; continue; }
                    actions.Add(invocation);
                    prevent |= capture.Binding.PreventDefault;
                    if (!capture.Binding.Fallthrough)
                    {
                        if (input.Type == KeyEventType.Press) ClearPending();
                        return Result(true, prevent, actions, KeymapDispatchReason.Handled);
                    }
                }
            }
            // Primary layout matches outrank fallback even when a pending command rejects.
            if (pending || input.Type == KeyEventType.Press && actions.Count > 0) break;
        }
        if (input.Type == KeyEventType.Press) ClearPending();
        return Result(actions.Count > 0, prevent, actions, actions.Count > 0 ? KeymapDispatchReason.Handled
            : rejected ? KeymapDispatchReason.Rejected : pending ? KeymapDispatchReason.SequenceMiss : KeymapDispatchReason.NoMatch);
    }

    public KeymapDispatchResult DispatchCommand(string id, KeymapLayerRegistry registry, KeymapContext context, string? input = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (var layer in registry.Active(context))
            foreach (var command in layer.Layer.Commands.Where(command => command.Name == id && command.Condition.Matches(context)))
            {
                var invocation = new KeymapInvocation(id, null, context, layer.Layer.Target, input);
                if (command.Run(invocation)) return Result(true, false, [invocation], KeymapDispatchReason.Handled);
            }
        return Result(false, false, [], KeymapDispatchReason.Rejected);
    }

    private static bool Reachable(Capture capture, KeymapLayerRegistry.Registration[] layers, KeymapContext context) =>
        capture.Binding.Condition.Matches(context) && (capture.Binding.Run is not null || capture.Binding.Command is null
            || layers.Any(layer => layer.Layer.Commands.Any(command => command.Name == capture.Binding.Command && command.Condition.Matches(context))));

    private static KeymapInvocation? Run(Capture capture, KeymapEvent input, KeymapLayerRegistry.Registration[] layers, KeymapContext context)
    {
        if (!capture.Binding.Condition.Matches(context)) return null;
        if (capture.Binding.Run is { } run)
        {
            var inline = new KeymapInvocation(null, input, context, capture.Layer.Layer.Target);
            return run(inline) ? inline : null;
        }
        foreach (var layer in layers)
            foreach (var command in layer.Layer.Commands.Where(command => command.Name == capture.Binding.Command && command.Condition.Matches(context)))
            {
                var invocation = new KeymapInvocation(command.Name, input, context, layer.Layer.Target ?? capture.Layer.Layer.Target);
                if (command.Run(invocation)) return invocation;
            }
        return null;
    }

    private void Arm(TimeSpan now) => Deadline = Pending.FirstOrDefault()?.Stroke == _options.TimedLeader && _options.TimedLeader is not null
        ? now + _options.LeaderTimeout : null;

    private KeymapDispatchResult Result(bool handled, bool prevent, IReadOnlyList<KeymapInvocation> actions, KeymapDispatchReason reason) =>
        new(handled, prevent, prevent, Pending, actions.ToArray(), reason);

    private sealed record Capture(KeymapLayerRegistry.Registration Layer, KeymapBinding Binding);
}
