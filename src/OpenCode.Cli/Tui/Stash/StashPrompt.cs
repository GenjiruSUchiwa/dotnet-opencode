namespace OpenCode.Cli.Tui.Stash;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCode.Cli.Tui.Attachments;
using OpenCode.Schema;

public sealed record StashPastedText(
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("source"), JsonRequired] PromptMention Source);

public sealed record StashRestoreData(PromptEditDocument Document, IReadOnlyList<StashPastedText> Pasted, JsonElement Source);

/// <summary>Lossless source PromptInfo. Parsing is intentionally shallow, as parsePromptInfo; typed restoration is explicit.</summary>
public sealed class StashPrompt
{
    private readonly JsonElement _value;
    private StashPrompt(JsonElement value) => _value = value.Clone();
    public string Text => _value.GetProperty("text").GetString()!;
    public JsonElement Value => _value.Clone();
    public bool HasAdmissionIdentity => new[] { "id", "messageID", "admissionID", "pendingAdmissionID" }.Any(name => _value.TryGetProperty(name, out _));

    public static StashPrompt? ParsePromptInfo(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
        && value.TryGetProperty("pasted", out var pasted) && pasted.ValueKind == JsonValueKind.Array ? new(value) : null;

    public static StashPrompt Capture(PromptEditDocument document, IReadOnlyList<StashPastedText> pasted)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pasted);
        var prompt = JsonSerializer.SerializeToNode(document.Input, OpenCodeJsonContext.Default.PromptInput)!.AsObject();
        prompt["pasted"] = JsonSerializer.SerializeToNode(pasted.ToArray(), StashJsonContext.Default.StashPastedTextArray);
        prompt["mode"] = document.ShellMode ? "shell" : "normal";
        // parsePromptInfo preserves extra fields. Keep native metadata/virtual marks in one
        // explicit extension without changing the source text/files/agents/skills/pasted shape.
        if (document.Metadata is not null || document.Marks is not null)
            prompt["$dotnet"] = JsonSerializer.SerializeToNode(new StashNativeEditor(1, document.Metadata, document.Marks), StashJsonContext.Default.StashNativeEditor);
        return new(JsonSerializer.SerializeToElement(prompt, StashJsonContext.Default.JsonObject));
    }

    /// <summary>Root must consume Document AND Pasted. Never silently degrade an unknown format or admission envelope to plain text.</summary>
    public StashRestoreData RestoreData()
    {
        if (HasAdmissionIdentity) throw new InvalidOperationException("This stash carries an admission identity; reconcile it instead of restoring it as a new request.");
        var unknown = _value.EnumerateObject().Where(item => item.Name is not ("text" or "files" or "agents" or "skills" or "pasted" or "mode" or "$dotnet"))
            .Select(item => item.Name).ToArray();
        if (unknown.Length > 0) throw new NotSupportedException("This prompt has additional source fields; use an explicit lossless restore adapter.");
        var mode = _value.TryGetProperty("mode", out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : null : "normal";
        if (mode is not ("normal" or "shell")) throw new NotSupportedException("The stored prompt mode is unsupported; the entry was not converted.");
        var input = _value.Deserialize(OpenCodeJsonContext.Default.PromptInput) ?? throw new JsonException("Missing structured prompt.");
        var pasted = _value.GetProperty("pasted").Deserialize(StashJsonContext.Default.StashPastedTextArray)
            ?? throw new JsonException("Missing pasted prompt descriptors.");
        if (pasted.Any(item => item is null || item.Text is null || item.Source is null)) throw new JsonException("Invalid pasted prompt descriptor.");
        var native = _value.TryGetProperty("$dotnet", out var extension)
            ? extension.Deserialize(StashJsonContext.Default.StashNativeEditor) ?? throw new JsonException("Invalid native prompt metadata.") : null;
        if (native is not null && native.Version != 1) throw new NotSupportedException("This native prompt metadata version is unsupported.");
        return new(new PromptEditDocument(input, native?.Metadata, native?.Marks, mode == "shell"), Array.AsReadOnly(pasted), _value.Clone());
    }
}

public sealed record StashEntry(StashPrompt Prompt, double Timestamp);

/// <summary>Root authority over the current editor/admission state. There is deliberately no default-allow overload.</summary>
public sealed record PromptStashAccess(bool Allowed, MessageId? AdmissionId, string? Reason = null)
{
    internal void RequireEditable()
    {
        if (!Allowed || AdmissionId is not null)
            throw new InvalidOperationException(Reason ?? "Resolve the bound or uncertain admission before stashing or replacing this draft.");
    }
}

internal sealed record StashNativeEditor(int Version, IReadOnlyDictionary<string, JsonElement>? Metadata = null, AttachmentMarksSnapshot? Marks = null);
internal sealed record StashDiskEntry([property: JsonPropertyName("prompt")] JsonElement Prompt, [property: JsonPropertyName("timestamp")] double Timestamp);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StashPastedText[]))]
[JsonSerializable(typeof(StashNativeEditor))]
[JsonSerializable(typeof(StashDiskEntry))]
[JsonSerializable(typeof(JsonObject))]
internal partial class StashJsonContext : JsonSerializerContext;
