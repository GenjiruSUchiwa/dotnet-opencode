namespace OpenCode.Cli.Tui.Models;

using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Schema;

public static class ModelPreferenceCatalog
{
    public static string? Unavailable(ModelInfo model, ProviderInfo? provider)
    {
        if (!model.Enabled) return "Model is disabled by the current catalog policy.";
        if (provider?.Activation == ProviderActivation.Disabled) return "Provider is disabled by the current catalog policy.";
        if (!AppCatalog.SupportsTransport(model, provider)) return "This model's transport is not implemented by the native client.";
        return null;
    }

    public static IReadOnlyList<DialogSelectOption<ModelRef>> Options(IReadOnlyList<ModelInfo> models,
        IReadOnlyList<ProviderInfo> providers, ModelPreferenceSnapshot preferences, string query, bool connected,
        ProviderId? filter, IReadOnlySet<string> favoritePriority, Func<ModelInfo, ProviderInfo?, string?>? unavailable = null)
    {
        var favorites = connected ? preferences.Favorite : [];
        var sections = connected && filter is null && query.Trim().Length == 0;
        var excluded = sections ? favorites.Concat(preferences.Recent).Select(ModelPreferences.Key).ToHashSet(StringComparer.Ordinal) : [];
        var favoriteKeys = favorites.Select(ModelPreferences.Key).ToHashSet(StringComparer.Ordinal);
        var rows = models.Where(model => model.Status != "deprecated" && (filter is null || model.ProviderId == filter))
            .OrderBy(model => model.ProviderId.Value != "opencode")
            .ThenBy(model => providers.FirstOrDefault(provider => provider.Id == model.ProviderId)?.Name ?? model.ProviderId.Value, StringComparer.CurrentCulture)
            .ThenByDescending(model => model.Time.Released).ThenBy(model => model.Name, StringComparer.CurrentCulture)
            .Where(model => !excluded.Contains(ModelPreferences.Key(new(model.ProviderId.Value, model.Id.Value))))
            .Select(model => Row(model, connected ? providers.FirstOrDefault(provider => provider.Id == model.ProviderId)?.Name ?? model.ProviderId.Value : null,
                favoriteKeys.Contains(ModelPreferences.Key(new(model.ProviderId.Value, model.Id.Value))) ? "(Favorite)" : null)).ToArray();
        if (query.Trim().Length > 0)
            return rows.Select((row, index) => (Row: row, Index: index, Score: DialogSearch.Score(query, row.Title, row.Category, null)))
                .Where(item => item.Score > 0).OrderByDescending(item => favoritePriority.Contains(ModelPreferences.Key(item.Row.Value)))
                .ThenByDescending(item => item.Score).ThenBy(item => item.Index).Select(item => item.Row).ToArray();
        if (!sections) return rows;
        return Section(favorites, "Favorites")
            .Concat(Section(preferences.Recent.Where(model => !favoriteKeys.Contains(ModelPreferences.Key(model))), "Recent"))
            .Concat(rows).ToArray();

        IEnumerable<DialogSelectOption<ModelRef>> Section(IEnumerable<ModelRef> entries, string title) => entries
            .Select(entry => models.FirstOrDefault(model => model.ProviderId.Value == entry.ProviderId && model.Id.Value == entry.Id))
            .OfType<ModelInfo>().Select(model => Row(model, title, providers.FirstOrDefault(provider => provider.Id == model.ProviderId)?.Name ?? model.ProviderId.Value));

        DialogSelectOption<ModelRef> Row(ModelInfo model, string? category, string? description)
        {
            var provider = providers.FirstOrDefault(provider => provider.Id == model.ProviderId);
            var reason = (unavailable ?? Unavailable)(model, provider);
            // Keep unavailable metadata visible and report the exact reason on selection. The
            // generic selector's Disabled flag removes rows, which would hide saved favorites.
            return new(new(model.ProviderId.Value, model.Id.Value), model.Name,
                reason is null ? description : string.IsNullOrEmpty(description) ? reason : description + " — " + reason,
                category, model.Cost.Count > 0 && model.Cost.All(cost => cost.Input.Amount == 0) ? "Free" : null);
        }
    }

    public static void RequireSelection(ModelRef selection, IReadOnlyList<ModelInfo> models, IReadOnlyList<ProviderInfo> providers,
        Func<ModelInfo, ProviderInfo?, string?>? unavailable = null)
    {
        var model = models.FirstOrDefault(model => model.ProviderId.Value == selection.ProviderId && model.Id.Value == selection.Id)
            ?? throw new InvalidOperationException($"Model is unavailable in the current catalog: {selection.ProviderId}/{selection.Id}");
        if ((unavailable ?? Unavailable)(model, providers.FirstOrDefault(provider => provider.Id == model.ProviderId)) is { } reason)
            throw new InvalidOperationException(reason);
        var variant = ModelPreferences.NormalizeVariant(selection.Variant);
        if (variant is not null && !model.Variants.Any(item => item.Id.Value == variant)) throw new InvalidOperationException($"Variant is unavailable for this model: {variant}");
    }
}

public sealed class ModelPreferencePersistenceException(ModelRef selection, Exception inner)
    : IOException($"Model {selection.ProviderId}/{selection.Id} was selected, but its preference could not be saved: {inner.Message}", inner)
{
    public ModelRef Selection { get; } = selection;
}

/// <summary>Typed accepted-choice/cycling boundary for root handlers. The callback owns actual model selection.</summary>
public sealed class ModelSelectionController(ModelPreferenceService preferences, Func<ModelRef, CancellationToken, Task> accept,
    Func<ModelInfo, ProviderInfo?, string?>? unavailable = null)
{
    public async Task SelectAsync(AcceptedModelSelection selection, IReadOnlyList<ModelInfo> models, IReadOnlyList<ProviderInfo> providers,
        CancellationToken ct = default)
    {
        ModelPreferenceCatalog.RequireSelection(selection.Model, models, providers, unavailable);
        await accept(selection.Model, ct);
        try { await preferences.RecordAcceptedAsync(selection, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OperationCanceledException)
        { throw new ModelPreferencePersistenceException(selection.Model, error); }
    }

    public async Task<ModelRef> CycleAsync(ModelRef? current, IReadOnlyList<ModelInfo> models, IReadOnlyList<ProviderInfo> providers,
        ModelPreferenceAction action, int direction = 1, CancellationToken ct = default)
    {
        if (!preferences.Loaded) await preferences.LoadAsync(ct);
        if (action == ModelPreferenceAction.VariantCycle && current is not null)
        {
            var model = models.FirstOrDefault(model => model.ProviderId.Value == current.ProviderId && model.Id.Value == current.Id)
                ?? throw new InvalidOperationException($"Model is unavailable in the current catalog: {current.ProviderId}/{current.Id}");
            if (model.Variants.Count == 0) return current;
            var value = ModelPreferences.NormalizeVariant(current.Variant);
            var selected = value is not null && model.Variants.Any(variant => variant.Id.Value == value) ? value : null;
            var variant = current with { Variant = ModelPreferences.CycleVariant(selected, model.Variants.Select(item => item.Id.Value)) };
            await SelectAsync(new(variant, action), models, providers, ct);
            return variant;
        }
        var next = action switch
        {
            ModelPreferenceAction.RecentCycle => ModelPreferences.CycleRecent(current, preferences.Value, models, direction),
            ModelPreferenceAction.FavoriteCycle => ModelPreferences.CycleFavorite(current, preferences.Value, models, direction),
            _ => throw new ArgumentException("A cycling action and current model are required.", nameof(action))
        };
        if (next is null) throw new InvalidOperationException(action == ModelPreferenceAction.FavoriteCycle
            ? "Add a favorite model to use this shortcut." : "No recently used model is available in the current catalog.");
        await SelectAsync(new(next, action), models, providers, ct);
        return next;
    }
}
