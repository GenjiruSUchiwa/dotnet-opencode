namespace OpenCode.Core.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ToolSearchEntry(string Path, string Type = "file");
public sealed record ToolSearchSubmatch(string Text, long Start, long End);
public sealed record ToolSearchMatch(ToolSearchEntry Entry, long Line, long Offset, string Text,
    IReadOnlyList<ToolSearchSubmatch> Submatches);

/// <summary>Source: core/src/ripgrep.ts. The host supplies a resolved ripgrep executable; no download or shell fallback.</summary>
public sealed class RipgrepProcess(string executable, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<IReadOnlyList<ToolSearchMatch>> GrepAsync(string cwd, string pattern, string? file,
        string? include, int limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd };
        foreach (var arg in new[] { "--no-config", "--json", "--hidden", "--no-messages" }) start.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(include)) start.ArgumentList.Add($"--glob={include}");
        start.ArgumentList.Add("--glob=!**/.git/**");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(pattern);
        start.ArgumentList.Add(file ?? ".");
        var matches = new List<ToolSearchMatch>();
        long retainedBytes = 0;
        var result = await RunAsync(start, line =>
        {
            if (line.Length == 0) return true;
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var type) || type.GetString() != "match") return true;
            retainedBytes += Encoding.UTF8.GetByteCount(line);
            if (retainedBytes > 20 * 1024 * 1024) throw new ToolExecutionException("Search results exceed the local 20 MiB capture limit. Narrow the path, pattern or result limit.");
            var row = document.RootElement.GetProperty("data").Deserialize<RawMatch>()
                ?? throw new JsonException("Invalid ripgrep match output.");
            if (row.Path?.Text is null || row.Lines?.Text is null || row.Line < 1 || row.Offset < 0 || row.Submatches is null)
                throw new JsonException("Invalid ripgrep match output.");
            var submatches = row.Submatches.Take(100).Select(item =>
                item.Match?.Text is not null && item.Start >= 0 && item.End >= item.Start && item.End <= Encoding.UTF8.GetByteCount(row.Lines.Text)
                    ? new ToolSearchSubmatch(item.Match.Text, item.Start, item.End)
                    : throw new JsonException("Invalid ripgrep submatch output.")).ToArray();
            matches.Add(new(new(Normalize(row.Path.Text)), row.Line, row.Offset,
                row.Lines.Text, submatches));
            return matches.Count <= limit;
        }, ct).ConfigureAwait(true);
        CheckExit(result, pattern);
        return result.ExitCode == 1 && !result.StoppedEarly ? [] : matches.Take(limit).ToArray();
    }

    public async Task<IReadOnlyList<ToolSearchEntry>> GlobAsync(string cwd, string pattern, int limit, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = cwd };
        foreach (var arg in new[] { "--no-config", "--files", $"--glob={pattern}", "--glob=!**/.git/**", "." })
            start.ArgumentList.Add(arg);
        var entries = new List<ToolSearchEntry>();
        long retainedBytes = 0;
        var result = await RunAsync(start, line =>
        {
            retainedBytes += Encoding.UTF8.GetByteCount(line);
            if (retainedBytes > 20 * 1024 * 1024) throw new ToolExecutionException("Search results exceed the local 20 MiB capture limit. Narrow the path, pattern or result limit.");
            if (line.Length > 0) entries.Add(new(Normalize(line)));
            return entries.Count <= limit;
        }, ct).ConfigureAwait(true);
        CheckExit(result, null);
        return result.ExitCode == 1 && !result.StoppedEarly ? [] : entries.Take(limit).ToArray();
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static void CheckExit(ToolProcessResult result, string? pattern)
    {
        if (result.StoppedEarly) return;
        if (pattern is not null && result.ExitCode == 2 &&
            (result.Error.Contains("regex parse error", StringComparison.Ordinal) || result.Error.Contains("error parsing regex", StringComparison.Ordinal)))
            throw new ToolExecutionException($"Invalid regex pattern: {result.Error.Trim()}");
        // Source accepts code 2 for partial searches with inaccessible files.
        if (result.ExitCode is not (0 or 1 or 2))
            throw new ToolExecutionException($"ripgrep failed with code {result.ExitCode}: {result.Error.Trim()}");
    }

    private async Task<ToolProcessResult> RunAsync(ProcessStartInfo start, Func<string, bool> onLine, CancellationToken ct)
    {
        try { return await OwnedToolProcess.RunLinesAsync(start, onLine, 30_000, ct, _clock).ConfigureAwait(true); }
        catch (TimeoutException error)
        {
            ct.ThrowIfCancellationRequested();
            throw new ToolExecutionException("Search timed out after 30 seconds. Consider using a more specific path or pattern.", error);
        }
    }

    private sealed record RawText([property: JsonPropertyName("text")] string? Text);
    private sealed record RawSubmatch(
        [property: JsonPropertyName("match"), JsonRequired] RawText? Match,
        [property: JsonPropertyName("start"), JsonRequired] long Start,
        [property: JsonPropertyName("end"), JsonRequired] long End);
    private sealed record RawMatch(
        [property: JsonPropertyName("path"), JsonRequired] RawText? Path,
        [property: JsonPropertyName("lines"), JsonRequired] RawText? Lines,
        [property: JsonPropertyName("line_number"), JsonRequired] long Line,
        [property: JsonPropertyName("absolute_offset"), JsonRequired] long Offset,
        [property: JsonPropertyName("submatches"), JsonRequired] RawSubmatch[]? Submatches);
}
