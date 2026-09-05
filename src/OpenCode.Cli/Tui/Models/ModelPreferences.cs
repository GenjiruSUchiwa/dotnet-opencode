namespace OpenCode.Cli.Tui.Models;

using System.Collections.Immutable;
using OpenCode.Schema;

public enum ModelPreferenceAction { Picker, RecentCycle, FavoriteCycle, VariantPicker, VariantCycle }
public sealed record AcceptedModelSelection(ModelRef Model, ModelPreferenceAction Action);
public sealed record ModelPreferenceSnapshot(ImmutableArray<ModelRef> Recent, ImmutableArray<ModelRef> Favorite,
    ImmutableDictionary<string, string> Variant)
{
    public static ModelPreferenceSnapshot Empty { get; } = new([], [], ImmutableDictionary<string, string>.Empty);
}

public static class ModelPreferences
{
    public static string Key(ModelRef model) => model.ProviderId + "/" + model.Id;
    public static string? NormalizeVariant(string? variant) => variant == "default" ? null : variant;
    public static ImmutableArray<ModelRef> Recent(ModelRef selected, IEnumerable<ModelRef> recent) =>
        recent.Prepend(selected).DistinctBy(Key).Take(10).Select(model => model with { Variant = null }).ToImmutableArray();

    public static ModelRef RetainVariant(ModelRef selected, ModelRef? current, ModelPreferenceSnapshot preferences,
        IReadOnlyList<ModelInfo> catalog, Func<ModelRef, string?>? preferred = null)
    {
        var variant = NormalizeVariant(current is not null && Key(current) == Key(selected) ? current.Variant
            : preferred is not null ? preferred(selected) : preferences.Variant.GetValueOrDefault(Key(selected)));
        var info = catalog.FirstOrDefault(model => model.ProviderId.Value == selected.ProviderId && model.Id.Value == selected.Id);
        return selected with { Variant = variant is not null && info?.Variants.Any(item => item.Id.Value == variant) == true ? variant : null };
    }

    public static ModelRef? CycleRecent(ModelRef? current, ModelPreferenceSnapshot preferences, IReadOnlyList<ModelInfo> catalog, int direction)
    {
        if (current is null) return null;
        return Cycle(Recent(current, preferences.Recent), current, catalog, direction, preferences);
    }

    public static ModelRef? CycleFavorite(ModelRef? current, ModelPreferenceSnapshot preferences, IReadOnlyList<ModelInfo> catalog, int direction) =>
        Cycle(preferences.Favorite, current, catalog, direction, preferences);

    private static ModelRef? Cycle(IEnumerable<ModelRef> items, ModelRef? current, IReadOnlyList<ModelInfo> catalog,
        int direction, ModelPreferenceSnapshot preferences)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        // Catalog presence governs source cycling. Do not delete unavailable preferences or silently
        // skip an existing but disabled/auth-failing candidate in favor of another model.
        var available = items.Where(item => catalog.Any(model => model.ProviderId.Value == item.ProviderId && model.Id.Value == item.Id)).ToArray();
        if (available.Length == 0) return null;
        var index = Array.FindIndex(available, item => current is not null && Key(item) == Key(current));
        var next = index < 0 ? direction == 1 ? 0 : available.Length - 1 : (index + direction + available.Length) % available.Length;
        return RetainVariant(available[next], current, preferences, catalog);
    }

    public static string? CycleVariant(string? current, IEnumerable<string> variants)
    {
        var named = variants.Where(variant => variant != "default").ToArray();
        if (named.Length == 0) return null;
        var selected = NormalizeVariant(current);
        if (selected is null) return named[0];
        var index = Array.IndexOf(named, selected);
        return index < 0 || index == named.Length - 1 ? null : named[index + 1];
    }
}

/// <summary>Source command IDs only. Key sequences come from the configured TUI keymap.</summary>
public static class ModelPreferenceCommands
{
    public const string Provider = "model.dialog.provider";
    public const string Favorite = "model.dialog.favorite";
    public const string RecentNext = "model.cycle_recent";
    public const string RecentPrevious = "model.cycle_recent_reverse";
    public const string FavoriteNext = "model.cycle_favorite";
    public const string FavoritePrevious = "model.cycle_favorite_reverse";
    public const string VariantCycle = "variant.cycle";
}
