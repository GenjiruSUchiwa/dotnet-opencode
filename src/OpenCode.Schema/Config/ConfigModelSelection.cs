namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Normalized Config.ModelSelection, deliberately using model rather than ModelRef.id.</summary>
[JsonConverter(typeof(ConfigModelSelectionJsonConverter))]
public sealed record ConfigModelSelection(string ProviderId, string Model, string? Variant = null)
{
    public string ProviderId { get; init => field = Validate(value, true); } = Validate(ProviderId, true);
    public string Model { get; init => field = Validate(value, false); } = Validate(Model, false);
    public string? Variant { get; init => field = value is null ? null : Validate(value, false); } = Variant is null ? null : Validate(Variant, false);
    private static string Validate(string value, bool provider)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('#') || (provider && value.Contains('/')))
            throw new JsonException("Invalid configuration model selection segment.");
        return value;
    }
    public static ConfigModelSelection Parse(string value)
    {
        var model = ModelRef.Parse(value);
        return new ConfigModelSelection(model.ProviderId, model.Id, model.Variant);
    }
}
