namespace OpenCode.Core.CodeMode;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Jint.Native;

/// <summary>Source PromiseRuntime's creation-order diagnostics and permanent observation.</summary>
internal sealed class CodeModePromiseObservations
{
    private sealed class Entry
    {
        internal long? Ordinal;
        internal bool Observed;
        internal CodeModeDiagnostic? Failure;
    }
    private readonly ConditionalWeakTable<JsValue, Entry> _entries = new();
    private readonly List<Entry> _tracked = [];
    private long _next;
    private bool _frozen;
    internal bool Frozen => _frozen;

    internal long Reserve() => _next++;

    internal JsValue Track(JsValue promise, long ordinal)
    {
        var entry = _entries.GetValue(promise, static _ => new Entry());
        if (entry.Ordinal is null)
        {
            entry.Ordinal = ordinal;
            _tracked.Add(entry);
        }
        else entry.Ordinal = Math.Min(entry.Ordinal.Value, ordinal);
        return promise;
    }

    internal JsValue Observe(JsValue value)
    {
        var entry = _entries.GetValue(value, static _ => new Entry());
        entry.Observed = true;
        entry.Failure = null;
        return value;
    }

    internal void Rejected(JsValue promise, CodeModeDiagnostic diagnostic)
    {
        if (_frozen) return;
        var entry = _entries.GetValue(promise, static _ => new Entry());
        if (entry.Observed) return;
        // Freeze text at rejection time, not later after a user mutates its error.
        entry.Failure = diagnostic with { Message = "Unhandled rejection from an un-awaited promise: " + diagnostic.Message };
    }

    internal ImmutableArray<CodeModeDiagnostic> Snapshot()
    {
        _frozen = true;
        return _tracked.Where(entry => !entry.Observed && entry.Failure is not null)
            .OrderBy(entry => entry.Ordinal).Select(entry => entry.Failure!).ToImmutableArray();
    }
}
