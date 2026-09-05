namespace OpenCode.Cli.Tui.Models;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Source model.json shape under the independent dotnet state channel. Construction does no I/O.</summary>
public sealed class ModelPreferenceStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static string Root => Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } root
        ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "opencode", OpenCodeChannel.Name);
    public string FilePath => Path.Combine(Root, "model.json");

    public async Task<ModelPreferenceSnapshot> LoadAsync(CancellationToken ct = default) => Decode(await ReadAsync(ct));

    public Task<ModelPreferenceSnapshot> ToggleFavoriteAsync(ModelRef model, CancellationToken ct = default) => UpdateAsync(document =>
    {
        var favorite = Array(document, "favorite");
        var matches = favorite.OfType<JsonObject>().Where(item => Ref(item) is { } value && ModelPreferences.Key(value) == ModelPreferences.Key(model)).ToArray();
        if (matches.Length == 0) favorite.Insert(0, Encode(model));
        else foreach (var item in matches) favorite.Remove(item);
    }, ct);

    public Task<ModelPreferenceSnapshot> RecordAcceptedAsync(AcceptedModelSelection accepted, CancellationToken ct = default)
    {
        if (accepted.Action == ModelPreferenceAction.RecentCycle) return LoadAsync(ct);
        return UpdateAsync(document =>
        {
            if (accepted.Action is ModelPreferenceAction.Picker or ModelPreferenceAction.FavoriteCycle)
            {
                var recent = Array(document, "recent");
                var selected = ModelPreferences.Recent(accepted.Model, recent.OfType<JsonObject>().Select(Ref).OfType<ModelRef>());
                document["recent"] = new JsonArray(selected.Select(model => (JsonNode)Encode(model)).ToArray());
            }
            if (accepted.Action is ModelPreferenceAction.VariantPicker or ModelPreferenceAction.VariantCycle)
            {
                var variants = Map(document, "variant");
                var key = ModelPreferences.Key(accepted.Model);
                var variant = ModelPreferences.NormalizeVariant(accepted.Model.Variant);
                if (variant is null) variants.Remove(key);
                else variants[key] = variant;
            }
        }, ct);
    }

    private async Task<ModelPreferenceSnapshot> UpdateAsync(Action<JsonObject> mutate, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "locks"));
        using var deadline = _clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        FileStream? ownership = null;
        while (ownership is null)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { ownership = new FileStream(Path.Combine(Root, "locks", "model-dotnet.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(TimeSpan.FromMilliseconds(50), _clock, deadline.Token); }
        }
        await using (ownership)
        {
            var document = await ReadAsync(deadline.Token);
            _ = Decode(document); // Refuse malformed containers before changing user state.
            mutate(document);
            var snapshot = Decode(document);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return snapshot;
        }
    }

    private async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(FilePath)) return new();
        return JsonNode.Parse(await File.ReadAllTextAsync(FilePath, ct)) as JsonObject
            ?? throw new JsonException("Model preferences must be an object; the file was not overwritten.");
    }

    private static ModelPreferenceSnapshot Decode(JsonObject document)
    {
        ImmutableArray<ModelRef> Models(string name)
        {
            if (document[name] is null) return [];
            if (document[name] is not JsonArray values) throw new JsonException($"Model preference {name} must be an array.");
            return values.OfType<JsonObject>().Select(Ref).OfType<ModelRef>().DistinctBy(ModelPreferences.Key).ToImmutableArray();
        }
        if (document["variant"] is not (null or JsonObject)) throw new JsonException("Model preference variant must be an object.");
        var variants = (document["variant"] as JsonObject ?? new()).Where(pair => pair.Key.Length > 0 && pair.Value is JsonValue value
                && value.TryGetValue<string>(out var text) && text.Length > 0 && text != "default")
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
        return new(Models("recent"), Models("favorite"), variants);
    }

    private static ModelRef? Ref(JsonObject value) => value["providerID"] is JsonValue provider && provider.TryGetValue<string>(out var providerId)
        && providerId.Length > 0 && value["modelID"] is JsonValue model && model.TryGetValue<string>(out var modelId) && modelId.Length > 0
        ? new(providerId, modelId) : null;
    private static JsonObject Encode(ModelRef model) => new() { ["providerID"] = model.ProviderId, ["modelID"] = model.Id };
    private static JsonArray Array(JsonObject document, string key)
    {
        if (document[key] is null) document[key] = new JsonArray();
        return document[key] as JsonArray ?? throw new JsonException($"Model preference {key} must be an array.");
    }
    private static JsonObject Map(JsonObject document, string key)
    {
        if (document[key] is null) document[key] = new JsonObject();
        return document[key] as JsonObject ?? throw new JsonException($"Model preference {key} must be an object.");
    }
}
