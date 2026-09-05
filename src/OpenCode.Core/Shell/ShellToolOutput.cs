namespace OpenCode.Core.Shell;

using System.Globalization;
using System.Text.Json;
using OpenCode.Core.Tools;
using OpenCode.Schema;

internal sealed record ShellToolOutput(string Output, bool Truncated, string Status, double? Exit = null, bool? Timeout = null, string? ShellId = null)
{
    public const string BackgroundInstruction = "You will be notified automatically when the command finishes. The notification will include the command's output. DO NOT run sleep commands or poll the output file to check for completion. You can read from the file when its current output would be useful, such as when inspecting logs from a background server. Otherwise, continue with other work or end your response.";

    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"exit":{"type":"number"},"shellID":{"type":"string"},
          "truncated":{"type":"boolean"},"timeout":{"type":"boolean"},"output":{"type":"string"},
          "status":{"enum":["completed","running"]}},"required":["output","truncated"]}
        """);

    public static ShellToolOutput Completed(ShellResult result, double timeout)
    {
        if (result.Capture is not { } capture) throw new ShellNotFoundException(result.Info.Id);
        return new(capture.Output + (result.Info.Status == ShellStatus.Timeout
                ? $"\n\nCommand exceeded timeout of {timeout.ToString(CultureInfo.InvariantCulture)} ms. Retry with a larger timeout if the command is expected to take longer." : ""),
            capture.Truncated, "completed", result.Info.Exit, result.Info.Status == ShellStatus.Timeout ? true : null);
    }

    public static ShellToolOutput Background(ShellInfo info) => new(
        $"Command moved to the background (shell ID: {info.Id.Value}).\nOutput is streaming to: {info.File}", false, "running", ShellId: info.Id.Value);

    public string[] Messages()
    {
        var notice = Status == "running" ? BackgroundInstruction : Timeout == true ? "Command timed out before completion."
            : Exit is { } exit ? $"Command exited with code {exit.ToString(CultureInfo.InvariantCulture)}." : null;
        return notice is null ? [Output] : [Output, notice];
    }

    public ToolExecutionResult ToolResult()
    {
        var metadata = Metadata();
        metadata["status"] = Status;
        if (ShellId is not null) metadata["shellID"] = ShellId;
        var value = new Dictionary<string, object>(metadata) { ["output"] = Output };
        return new ToolExecutionResult { Output = value, Metadata = metadata, Content = Messages().Select(text => (ToolContent)new ToolTextContent(text)).ToArray() };
    }

    public Dictionary<string, object> Metadata()
    {
        var result = new Dictionary<string, object> { ["truncated"] = Truncated };
        if (Exit is { } exit) result["exit"] = exit;
        if (Timeout is { } timeout) result["timeout"] = timeout;
        return result;
    }

    public static ShellNotification Notification(ShellInfo shell, ShellJobInfo job, ShellToolOutput? output)
    {
        var state = job.Status switch
        {
            ShellJobStatus.Completed => "completed", ShellJobStatus.Error => "error", ShellJobStatus.Cancelled => "cancelled",
            _ => throw new InvalidOperationException("A running shell job cannot publish a completion notification.")
        };
        var text = output is not null ? string.Join("\n\n", output.Messages()) : job.Status == ShellJobStatus.Error ? job.Error ?? "Command failed" : "Command cancelled";
        return ShellNotification.Background(job.Id, shell.Id, shell.Command, state, text,
            output?.Metadata().ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value), StringComparer.Ordinal));
    }
}
