namespace OpenCode.Cli.Tui.ToolViews;

using System.Text.Json;
using OpenCode.Schema;

public sealed record ToolPatchFile(int Index, string Type, string RelativePath, string FilePath, string Patch,
    double Additions, double Deletions, string? MovePath);
public sealed record ToolAppliedFile(int Index, string Type, string Resource);
public sealed record ToolDiagnostic(double Line, double Character, string Message);

/// <summary>The production tool-display parsers. Only actual canonical input/metadata is interpreted.</summary>
public static class ToolResultData
{
    public static string Kind(string name) => name switch { "bash" => "shell", "task" => "subagent", "apply_patch" => "patch", _ => name };
    public static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>();
    public static IReadOnlyDictionary<string, JsonElement> Input(AssistantToolContent part) => part.State switch
    {
        ToolStateRunning value => value.Input, ToolStateCompleted value => value.Input, ToolStateError value => value.Input, _ => Empty
    };
    public static IReadOnlyDictionary<string, JsonElement> Metadata(AssistantToolContent part) => part.State switch
    {
        ToolStateRunning value => value.Metadata ?? Empty, ToolStateCompleted value => value.Metadata ?? Empty,
        ToolStateError value => value.Metadata ?? Empty, _ => Empty
    };
    public static string? Text(IReadOnlyDictionary<string, JsonElement> data, string key) => data.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    public static double? Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item)
        && item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;
    public static IEnumerable<JsonElement> Array(IReadOnlyDictionary<string, JsonElement> data, string key) => data.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    public static IReadOnlyList<ToolPatchFile> Files(IReadOnlyDictionary<string, JsonElement> metadata) => Array(metadata, "files")
        .Select((file, index) =>
        {
            var type = Text(file, "type") ?? (Text(file, "status") switch { "added" => "add", "deleted" => "delete", "modified" => "update", _ => null });
            var relative = Text(file, "file") ?? Text(file, "relativePath");
            var path = Text(file, "filePath") ?? relative;
            return !string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(relative) && !string.IsNullOrEmpty(path)
                && Text(file, "patch") is { } patch && Number(file, "additions") is { } additions && Number(file, "deletions") is { } deletions
                ? new ToolPatchFile(index, type, relative, path, patch, additions, deletions, Text(file, "movePath")) : null;
        }).OfType<ToolPatchFile>().ToArray();

    public static IReadOnlyList<ToolAppliedFile> Applied(IReadOnlyDictionary<string, JsonElement> metadata) => Array(metadata, "applied")
        .Select((file, index) => Text(file, "type") is { Length: > 0 } type && Text(file, "resource") is { Length: > 0 } resource
            ? new ToolAppliedFile(index, type, resource) : null).OfType<ToolAppliedFile>().ToArray();

    public static IReadOnlyList<string> Questions(IReadOnlyDictionary<string, JsonElement> input) => Array(input, "questions")
        .Select(item => Text(item, "question")).OfType<string>().Where(text => text.Length > 0).ToArray();
    public static IReadOnlyList<IReadOnlyList<string>>? Answers(IReadOnlyDictionary<string, JsonElement> metadata) =>
        metadata.TryGetValue("answers", out var answers) && answers.ValueKind == JsonValueKind.Array
            ? answers.EnumerateArray().Select(answer => (IReadOnlyList<string>)(answer.ValueKind == JsonValueKind.Array
                ? answer.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [])).ToArray() : null;

    public static IReadOnlyList<ToolDiagnostic> Diagnostics(IReadOnlyDictionary<string, JsonElement> metadata, string path)
    {
        if (!metadata.TryGetValue("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Object
            || !diagnostics.TryGetProperty(path, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select(item =>
        {
            if (Number(item, "severity") != 1 || Text(item, "message") is not { Length: > 0 } message
                || !item.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object
                || !range.TryGetProperty("start", out var start) || Number(start, "line") is not { } line || Number(start, "character") is not { } character) return null;
            return new ToolDiagnostic(line, character, message);
        }).OfType<ToolDiagnostic>().Take(3).ToArray();
    }
}
