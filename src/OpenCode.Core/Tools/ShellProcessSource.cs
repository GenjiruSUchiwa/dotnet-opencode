namespace OpenCode.Core.Tools;

using System.Diagnostics;
using System.Text;

public sealed record ShellProcessOutput(string Output, string File, int? Exit, bool Truncated, bool Timeout);

/// <summary>Foreground .NET 11 process ownership and combined file-backed capture. No shell API, background job
/// registry or Session state is synthesized here. Only the process started by this source may be stopped.</summary>
public sealed class ShellProcessSource(string directory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<ShellProcessOutput> RunAsync(PreparedToolShell prepared, int timeout, CancellationToken ct,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Shell output directory must be absolute.", nameof(directory));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"sh_{Guid.NewGuid():N}.out");
        using var capture = File.OpenHandle(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(prepared.Executable)
        {
            WorkingDirectory = prepared.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true, StartDetached = false,
            InheritedHandles = [], StandardInputHandle = input,
            StandardOutputHandle = capture, StandardErrorHandle = capture
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var variable in environment) start.Environment[variable.Key] = variable.Value;
        }
        start.Environment["TERM"] = "xterm-256color";
        start.Environment["OPENCODE_TERMINAL"] = "1";
        foreach (var argument in prepared.Arguments) start.ArgumentList.Add(argument);
        using var lifetime = _clock.CreateLinkedCancellationTokenSource(ct);
        using var stopping = new CancellationTokenSource();
        if (timeout > 0) lifetime.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();
        process.Start();
        var exited = process.SafeHandle.WaitForExitOrKillOnCancellationAsync(stopping.Token);
        var timedOut = false;
        int? exit = null;
        try
        {
            var status = await exited.WaitAsync(lifetime.Token).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            exit = status.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        { timedOut = true; }
        finally
        {
            // Stop descendants before the root is reaped. A managed root wait does not kill a process tree.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(true);
                await exited.ConfigureAwait(true);
            }
        }
        ct.ThrowIfCancellationRequested();
        // Both native standard handles share one backing file, with no reader task that can wait forever for
        // an inherited pipe after the root exits. Read a bounded snapshot, even if a detached descendant writes.
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous);
        await using var streamLifetime = stream.ConfigureAwait(true);
        const int maximumBytes = 50 * 1024;
        const int maximumLines = 2000;
        var size = stream.Length;
        var bytes = new byte[(int)Math.Min(size, maximumBytes)];
        stream.Position = Math.Max(0, size - maximumBytes);
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(true);
        var offset = 0;
        if (size > maximumBytes)
            while (offset < count && (bytes[offset] & 0xc0) == 0x80) offset++;
        var text = Encoding.UTF8.GetString(bytes, offset, count - offset);
        var lines = text.Split('\n');
        var lineCount = lines.Length - (text.EndsWith('\n') ? 1 : 0);
        var truncated = size > maximumBytes || lineCount > maximumLines;
        if (lineCount > maximumLines) text = string.Join('\n', lines.Skip(lineCount - maximumLines));
        if (text.Length == 0) text = "(no output)";
        if (truncated) text += $"\n\n[output truncated; full output saved to: {file}]";
        if (timedOut) text += $"\n\nCommand exceeded timeout of {timeout} ms. Retry with a larger timeout if the command is expected to take longer.";
        return new(text, file, exit, truncated, timedOut);
    }
}
