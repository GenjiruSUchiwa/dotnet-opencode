# Porting Ruleset 05: Process and Terminal APIs

This rulebook defines how to handle process spawning, process locks, and pseudoterminals (PTY) in .NET 10.

---

## 1. Process Execution with Modern .NET 10

.NET 10 offers high-performance, asynchronous, cross-platform process management.

### Guidelines
1. **Never use `UseShellExecute = true`** for running commands. Always set `UseShellExecute = false`.
2. **Argument Escaping**: Use `ArgumentList.Add(...)` rather than string concatenation in `Arguments`. This prevents shell injection vulnerabilities across Windows, macOS, and Linux.
3. **Async Streaming**: Stream stdout and stderr using `StandardOutput.BaseStream.ReadAsync(...)` or lines via `StandardOutput.ReadLineAsync(...)`.
4. **Tree Kill**: When canceling or terminating child processes, always use `process.Kill(entireProcessTree: true)` so orphaned background tools do not leak.

### Process Runner Implementation
```csharp
namespace OpenCode.Core.Util;

using System.Diagnostics;

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr);

public static class ProcessRunner
{
    public static async Task<ProcessOutput> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessOutput(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process already exited
            }
            throw;
        }
    }
}
```

---

## 2. Cross-Platform Process Locks

In TypeScript, `process-lock` prevents conflicting daemon runs on the same state folder using native file locking.

In .NET:
- On Windows: Use `FileStream` with `FileShare.None`.
- On Unix/macOS: `FileStream` with `FileShare.None` maps to `flock(fd, LOCK_EX)`.

```csharp
namespace OpenCode.Core.Util;

public sealed class ProcessLock : IAsyncDisposable
{
    private readonly FileStream _lockStream;

    private ProcessLock(FileStream lockStream)
    {
        _lockStream = lockStream;
    }

    public static ProcessLock? TryAcquire(string lockFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(lockFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            return new ProcessLock(stream);
        }
        catch (IOException)
        {
            // Lock is held by another process
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _lockStream.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

---

## 3. Pseudoterminal (PTY) and ConPTY

OpenCode runs interactive shells and background tasks inside a PTY (`@opencode-ai/pty`, `node-pty`, or `bun-pty`).

### Windows ConPTY via `[LibraryImport]`
Windows 10+ natively provides the PseudoConsole API:

```csharp
namespace OpenCode.Core.Pty;

using System.Runtime.InteropServices;

internal static partial class WindowsConPty
{
    public const int S_OK = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int CreatePseudoConsole(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int ResizePseudoConsole(IntPtr hPC, Coord size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial int ClosePseudoConsole(IntPtr hPC);

    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }
}
```

### High-Level PTY Interface
Provide a unified cross-platform interface:

```csharp
public interface IPtySession : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task<int> WaitForExitAsync(CancellationToken ct = default);
    void Resize(int columns, int rows);
    void Kill();
}
```

- On Windows: Wraps Windows ConPTY (`CreatePseudoConsole`).
- On Linux/macOS: Wraps `openpty` via standard libc P/Invoke.
