namespace OpenCode.Core.Shell;

using System.Text;
using System.Text.Json;
using OpenCode.Schema;

public sealed class ShellNotFoundException(ShellId id) : Exception($"Shell command not found: {id.Value}")
{
    public ShellId Id { get; } = id;
}

public sealed class ShellOutputUnavailableException(ShellId id, Exception inner) : IOException("Shell command output is no longer available.", inner)
{
    public ShellId Id { get; } = id;
}

public sealed record ShellCapture(string Output, bool Truncated);
public sealed record ShellResult(ShellInfo Info, ShellCapture? Capture)
{
    public const string MissingOutput = "Shell command output is no longer available.";
    public static ShellOutput Unavailable => new(MissingOutput, Encoding.UTF8.GetByteCount(MissingOutput), Encoding.UTF8.GetByteCount(MissingOutput), false);

    public ShellNotification UserNotification()
    {
        var state = Info.Status == ShellStatus.Killed ? "cancelled" : "completed";
        var notice = Info.Status == ShellStatus.Killed ? "Command cancelled."
            : Info.Status == ShellStatus.Timeout ? "Command timed out before completion."
            : Info.Exit is { } exit ? $"Command exited with code {exit}." : "Command exited with code unknown.";
        var text = $"The following shell command was executed by the user:\n<shell id=\"{Info.Id.Value}\" state=\"{state}\" command=\"{Info.Command}\">\n{Capture?.Output ?? MissingOutput}\n\n{notice}\n</shell>";
        var metadata = new Dictionary<string, JsonElement>
        {
            ["source"] = JsonSerializer.SerializeToElement("shell"), ["shellID"] = JsonSerializer.SerializeToElement(Info.Id.Value),
            ["state"] = JsonSerializer.SerializeToElement(state), ["truncated"] = JsonSerializer.SerializeToElement(Capture?.Truncated ?? false)
        };
        if (Info.Exit is { } code) metadata["exit"] = JsonSerializer.SerializeToElement(code);
        if (Info.Status == ShellStatus.Timeout) metadata["timeout"] = JsonSerializer.SerializeToElement(true);
        return new(text, Info.Command, metadata);
    }
}

public sealed record ShellNotification(string Text, string Description, IReadOnlyDictionary<string, JsonElement> Metadata)
{
    public static ShellNotification Background(string jobId, ShellId shellId, string command, string state, string text,
        IReadOnlyDictionary<string, JsonElement>? outputMetadata = null)
    {
        if (state is not ("completed" or "error" or "cancelled")) throw new ArgumentException("A shell completion requires a terminal job state.", nameof(state));
        var metadata = outputMetadata?.ToDictionary(item => item.Key, item => item.Value.Clone(), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        metadata["source"] = JsonSerializer.SerializeToElement("shell");
        metadata["shellID"] = JsonSerializer.SerializeToElement(shellId.Value);
        metadata["jobID"] = JsonSerializer.SerializeToElement(jobId);
        metadata["state"] = JsonSerializer.SerializeToElement(state);
        return new($"<shell id=\"{jobId}\" state=\"{state}\" command=\"{command}\">\n{text}\n</shell>", command, metadata);
    }
}

/// <summary>
/// Implement in the existing Session aggregate/event boundary. Resolve the Session's current
/// Location when publishing; never pin durable events to the shell's original execution Location.
/// No implementation or SQL fallback is supplied by the Shell domain.
/// </summary>
public interface ISessionShellLifecycle
{
    Task StartedAsync(SessionId sessionId, EventId? eventId, ShellInfo shell, CancellationToken ct);
    Task EndedAsync(SessionId sessionId, ShellInfo shell, ShellOutput output, CancellationToken ct);
    /// <summary>Durably admit a synthetic message with resume:false, without starting a model runner.</summary>
    Task NotifyAsync(SessionId sessionId, ShellNotification notification, CancellationToken ct);
}
