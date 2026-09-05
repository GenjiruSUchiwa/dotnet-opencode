namespace OpenCode.Cli.Tui.Models;

using OpenCode.Schema;

/// <summary>One host-owned preference view. Only explicit favorite edits and accepted user choices mutate storage.</summary>
public sealed class ModelPreferenceService(ModelPreferenceStore? store = null, TimeProvider? clock = null)
{
    private readonly ModelPreferenceStore _store = store ?? new(clock);
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ModelPreferenceSnapshot Value { get; private set; } = ModelPreferenceSnapshot.Empty;
    public bool Loaded { get; private set; }
    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { Value = await _store.LoadAsync(ct); Loaded = true; }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task ToggleFavoriteAsync(ModelRef model, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { Value = await _store.ToggleFavoriteAsync(model with { Variant = null }, ct); Loaded = true; }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    public async Task RecordAcceptedAsync(AcceptedModelSelection selection, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { Value = await _store.RecordAcceptedAsync(selection, ct); Loaded = true; }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
}
