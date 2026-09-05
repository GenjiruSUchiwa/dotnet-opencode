namespace OpenCode.Core.Tools;
using Transport;

using System.Diagnostics;
using System.Text;

internal sealed record ToolProcessResult(int ExitCode, string Error, bool StoppedEarly, bool ErrorTruncated);

/// <summary>Owns only the process it starts and its redirected readers, including early search cutoff.</summary>
internal static class OwnedToolProcess
{
    public static async Task<ToolProcessResult> RunAsync(ProcessStartInfo start,
        Func<ReadOnlyMemory<char>, bool>? onChunk, int timeout, CancellationToken ct, TimeProvider clock)
    {
        ct.ThrowIfCancellationRequested();
        using var nullHandle = File.OpenNullHandle();
        start.UseShellExecute = false;
        start.StartDetached = false;
        start.InheritedHandles = [];
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        start.RedirectStandardInput = false;
        start.StandardInputEncoding = null;
        start.StandardInputHandle = nullHandle;
        start.RedirectStandardOutput = onChunk is not null;
        start.RedirectStandardError = onChunk is not null;
        start.StandardOutputHandle = onChunk is null ? nullHandle : null;
        start.StandardErrorHandle = onChunk is null ? nullHandle : null;
        if (onChunk is null) { start.StandardOutputEncoding = null; start.StandardErrorEncoding = null; }
        start.CreateNoWindow = true;
        using var lifetime = clock.CreateLinkedCancellationTokenSource(ct);
        using var stopping = new CancellationTokenSource();
        if (timeout > 0) lifetime.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        process.Start();
        // Keep this kill token separate from reader timeout/cancellation: terminate the tree first,
        // then let the managed SafeProcessHandle API kill/reap the owned root if necessary.
        var exited = process.SafeHandle.WaitForExitOrKillOnCancellationAsync(stopping.Token);
        var errors = new StringBuilder();
        var errorTruncated = false;
        var output = onChunk is null ? Task.FromResult(false) : ReadOutputAsync();
        var error = onChunk is null ? Task.CompletedTask : ReadErrorAsync();
        try
        {
            var stopped = await output;
            if (stopped) RequestStop();
            var status = await exited.WaitAsync(lifetime.Token);
            await error;
            ct.ThrowIfCancellationRequested();
            // Cancellation used for a search limit is expected, not a caller interruption or timeout.
            if (status.Canceled && !stopped)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                throw new OperationCanceledException("Owned process was cancelled.", stopping.Token);
            }
            return new(status.ExitCode, errors.ToString(), stopped, errorTruncated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            throw new TimeoutException($"Process timed out after {timeout} ms.");
        }
        finally
        {
            try { RequestStop(); }
            finally
            {
                lifetime.Cancel();
                try { await exited; }
                finally { await Task.WhenAll(output, error).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
            }
        }

        void RequestStop()
        {
            if (stopping.IsCancellationRequested) return;
            try
            {
                // SafeProcessHandle.Kill targets only the root; this is the remaining tree-specific work.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            finally { stopping.Cancel(); }
        }

        async Task<bool> ReadOutputAsync()
        {
            await foreach (var chunk in PipelineText.ChunksAsync(process.StandardOutput.BaseStream, process.StandardOutput.CurrentEncoding,
                cancellationToken: lifetime.Token))
                if (!onChunk!(chunk.AsMemory())) return true;
            return false;
        }

        async Task ReadErrorAsync()
        {
            await foreach (var chunk in PipelineText.ChunksAsync(process.StandardError.BaseStream, process.StandardError.CurrentEncoding,
                cancellationToken: lifetime.Token))
            {
                var available = 8192 - errors.Length;
                if (chunk.Length > available) errorTruncated = true;
                if (available > 0) errors.Append(chunk.AsSpan(0, Math.Min(chunk.Length, available)));
            }
        }
    }

    public static async Task<ToolProcessResult> RunLinesAsync(ProcessStartInfo start,
        Func<string, bool> onLine, int timeout, CancellationToken ct, TimeProvider clock)
    {
        const int maximumRecordCharacters = 1024 * 1024;
        using var records = new TextRecordBuffer(maximumRecordCharacters, lfOnly: true,
            tooLarge: () => new IOException($"Search record exceeds {maximumRecordCharacters} UTF-16 characters; narrow the search. No partial record was returned."));
        var result = await RunAsync(start, chunk =>
        {
            records.Append(chunk.Span);
            while (records.TryRead(out var record))
            {
                var line = record.Text;
                if (line.EndsWith('\r')) line = line[..^1];
                if (!onLine(line)) return false;
            }
            return true;
        }, timeout, ct, clock);
        if (!result.StoppedEarly && records.TryRead(out var final, completed: true))
        {
            var line = final.Text;
            onLine(line.EndsWith('\r') ? line[..^1] : line);
        }
        return result;
    }
}
