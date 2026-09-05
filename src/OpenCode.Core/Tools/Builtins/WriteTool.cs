namespace OpenCode.Core.Tools.Builtins;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// Source-backed write leaf; requires real Location mutation services.
/// </summary>
public sealed class WriteTool(ToolFilePolicy? policy = null, IToolFileMutation? mutation = null)
{
    public ToolInfo Create() => ToolInfo.FromJson(Name, Description, InputSchema, ExecuteAsync, BuiltinToolSchemas.Write,
        new ToolOptions(Permission: "edit", CodeMode: false));
    public string Name => "write";

    public string Description =>
        "Writes a file to the local filesystem, overwriting if one exists. Missing parent directories are created automatically. " +
        "Use this tool to create new files or overwrite existing files. For partial changes, use the edit tool instead.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "Path to the file to write to" },
            "content": { "type": "string", "description": "Content to write to the file" }
        },
        "required": ["path", "content"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var args = new ToolInput(input);
        var path = args.String("path");
        var content = args.String("content");
        if (policy is null || mutation is null)
            throw new NotSupportedException("write requires Location, permission and file mutation services.");
        if (Encoding.UTF8.GetByteCount(content) > mutation.MaximumBytes)
            throw new ToolExecutionException($"Content exceeds the configured {mutation.MaximumBytes} byte limit.");
        try
        {
            var target = await policy.ResolveAsync(path, ToolPathKind.File, context, ct).ConfigureAwait(true);
            var transaction = await mutation.LockAsync(target.Absolute, ct).ConfigureAwait(true);
            await using var transactionLifetime = transaction.ConfigureAwait(true);
            var original = await transaction.ReadAsync(ct).ConfigureAwait(true);
            var existed = original is not null;
            var preview = mutation.Diff(target.Resource, original?.Text ?? "", content.TrimStart('\uFEFF'),
                existed ? FileDiffStatus.Modified : FileDiffStatus.Added);
            await policy.AssertAsync("edit", [target.Resource], ["*"], context,
                new Dictionary<string, object> { ["files"] = new[] { preview } }, ct).ConfigureAwait(true);
            await transaction.WriteTextAsync(content, ct).ConfigureAwait(true);
            // The transaction settles an admitted write/formatter/BOM repair before releasing
            // its lock. Do not report a completed tool if its caller was interrupted meanwhile.
            ct.ThrowIfCancellationRequested();
            return new ToolExecutionResult($"{(existed ? "Wrote" : "Created")} file successfully: {target.Resource}",
                new { operation = "write", target = target.Absolute, resource = target.Resource, existed });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ct.ThrowIfCancellationRequested();
            throw new ToolExecutionException($"Unable to write {path}", error);
        }
    }
}
