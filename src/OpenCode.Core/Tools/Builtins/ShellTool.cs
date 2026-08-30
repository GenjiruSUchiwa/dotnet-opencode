namespace OpenCode.Core.Tools.Builtins;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of packages/core/src/tool/plugin/shell.ts
/// </summary>
public sealed class ShellTool : ITool
{
    public string Name => "shell";

    public string Description =>
        "Execute a shell command and return its output. Commands run on Windows using pwsh or powershell, and bash on Unix. Quote file paths containing spaces or special characters.";

    public JsonElement InputSchema => JsonDocument.Parse("""
    {
        "type": "object",
        "properties": {
            "command": { "type": "string", "description": "Shell command string to execute" },
            "timeout": { "type": "integer", "description": "Timeout in milliseconds. Set to 0 to disable. Defaults to 120000." },
            "workdir": { "type": "string", "description": "Working directory to execute the command in. Defaults to current directory." }
        },
        "required": ["command"]
    }
    """).RootElement;

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var command = input.GetProperty("command").GetString()!;
        var timeoutMs = input.TryGetProperty("timeout", out var tProp) ? tProp.GetInt32() : 120000;
        if (timeoutMs <= 0) timeoutMs = 120000;

        var workdir = input.TryGetProperty("workdir", out var wProp) && !string.IsNullOrEmpty(wProp.GetString())
            ? wProp.GetString()!
            : Directory.GetCurrentDirectory();

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var shellFileName = isWindows ? "powershell.exe" : "/bin/bash";
        var shellArgs = isWindows ? new[] { "-NoProfile", "-Command", command } : new[] { "-c", command };

        var startInfo = new ProcessStartInfo
        {
            FileName = shellFileName,
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in shellArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = (stdout + "\n" + stderr).Trim();
            if (string.IsNullOrEmpty(output))
            {
                output = $"(Command completed with exit code {process.ExitCode})";
            }

            return new ToolExecutionResult(output, Output: new { exitCode = process.ExitCode });
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Command timed out after {timeoutMs} ms: {command}");
        }
    }
}
