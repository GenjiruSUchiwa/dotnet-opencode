namespace OpenCode.Core.Shell;

using OpenCode.Schema;

public enum ShellJobStatus { Running, Completed, Error, Cancelled }
public sealed record ShellJobInfo(string Id, ShellJobStatus Status, MessageId? NotificationId = null, string? Error = null);
public sealed record ShellJobBlock(ShellJobInfo Info, bool Backgrounded);

/// <summary>Adapt to Job.start with id=shell.ID, type=shell, title=command and recovery.kind=shell.</summary>
public sealed record ShellJobStart(ShellInfo Shell, SessionId SessionId, Func<CancellationToken, Task<string>> Run);

/// <summary>
/// Adapter to the host's canonical Job/PluginRuntime and Session admission services. No memory-only
/// implementation is provided here. Start owns Run independently of the submitting tool's token;
/// cancelling that job must cancel Run. Unknown cancellation is a no-op.
/// </summary>
public interface IShellToolJobs
{
    /// <summary>True only with durable background markers, stable notification IDs and real completion admission/recovery.</summary>
    bool SupportsBackground { get; }
    Task<ShellJobInfo> StartAsync(ShellJobStart input, CancellationToken ct);
    Task<ShellJobBlock?> BlockAsync(string id, SessionId sessionId, CancellationToken ct);
    /// <summary>Commit the recovery marker before returning its stable NotificationId. Do not report a cancelled wait after commit.</summary>
    Task<ShellJobInfo?> BackgroundAsync(string id, CancellationToken ct);
    Task<ShellJobInfo?> WaitAsync(string id, CancellationToken ct);
    Task CancelAsync(string id, CancellationToken ct);
    /// <summary>Reconcile/admit by notificationId and wake after commit. Model background notifications resume, unlike user session.shell.</summary>
    Task AdmitCompletionAsync(SessionId sessionId, MessageId notificationId, ShellNotification notification, CancellationToken ct);
    /// <summary>Remove the durable background marker only after completion admission succeeds.</summary>
    Task CompleteBackgroundAsync(MessageId notificationId, CancellationToken ct);
}
