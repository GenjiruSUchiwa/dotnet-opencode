namespace OpenCode.Core.Shell;

using System.Diagnostics;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>ShellTool producer orchestration, not another tool registry or an in-memory replacement for Job.</summary>
internal static class ShellToolExecution
{
    public static async Task<ToolExecutionResult> ExecuteAsync(ShellRuntime runtime, IToolShellPolicy policy, IShellToolJobs? jobs,
        string command, string? workdir, int timeout, bool background, ToolContext context, CancellationToken ct)
    {
        if (background && jobs?.SupportsBackground != true)
            throw new ToolExecutionException("Background shell execution requires the host's durable Job and Session completion-notification adapter.");
        var started = await runtime.CreateToolAsync(new ShellCreateInput(command, timeout, workdir), context, policy, ct).ConfigureAwait(true);
        var retained = false;
        var jobRequested = false;
        try
        {
            await context.ReportProgress(new Dictionary<string, object> { ["shellID"] = started.Id.Value }).WaitAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            if (jobs is null)
            {
                var output = ShellToolOutput.Completed(await runtime.ResultAsync(started, ct: ct).ConfigureAwait(true), timeout);
                ct.ThrowIfCancellationRequested();
                retained = true;
                return output.ToolResult();
            }

            var settled = new TaskCompletionSource<ShellToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
            jobRequested = true;
            var job = await jobs.StartAsync(new ShellJobStart(started, context.SessionId, async token =>
            {
                try
                {
                    var output = ShellToolOutput.Completed(await runtime.ResultAsync(started, ct: token).ConfigureAwait(true), timeout);
                    token.ThrowIfCancellationRequested();
                    settled.TrySetResult(output);
                    return string.Join("\n\n", output.Messages());
                }
                catch (OperationCanceledException)
                {
                    await RemoveOwnedAsync(runtime, started.Id).ConfigureAwait(true);
                    throw;
                }
            }), ct).WaitAsync(ct).ConfigureAwait(true);
            if (job.Id != started.Id.Value) throw new InvalidOperationException("The shell Job adapter did not preserve the supplied shell-ID job identity.");
            ct.ThrowIfCancellationRequested();
            if (background)
            {
                var handoff = await jobs.BackgroundAsync(job.Id, ct).ConfigureAwait(true);
                RequireBackground(jobs, handoff, started);
                // BackgroundAsync committed ownership/recovery. A late tool cancellation must not
                // cancel the independently owned job after that handoff.
                retained = true;
                Watch(runtime, jobs, started, context.SessionId, handoff!, settled.Task);
                return ShellToolOutput.Background(started).ToolResult();
            }

            var result = await jobs.BlockAsync(job.Id, context.SessionId, ct).WaitAsync(ct).ConfigureAwait(true)
                ?? throw new ToolExecutionException("The shell job is no longer available.");
            if (result.Backgrounded)
            {
                RequireBackground(jobs, result.Info, started);
                retained = true;
                // Only promotion clears the foreground timeout; explicit background requests keep
                // an explicitly supplied timeout, matching source ShellTool.
                await runtime.TimeoutAsync(started.Id, 0, CancellationToken.None).ConfigureAwait(true);
                Watch(runtime, jobs, started, context.SessionId, result.Info, settled.Task);
                return ShellToolOutput.Background(started).ToolResult();
            }
            ct.ThrowIfCancellationRequested();
            if (result.Info.Id != job.Id) throw new InvalidOperationException("The shell Job adapter returned a different job.");
            if (result.Info.Status == ShellJobStatus.Error) throw new ToolExecutionException(result.Info.Error ?? "Command failed");
            if (result.Info.Status == ShellJobStatus.Cancelled) throw new ToolExecutionException("Command cancelled");
            if (result.Info.Status != ShellJobStatus.Completed || !settled.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("The shell Job adapter completed before its real command result was captured.");
            ct.ThrowIfCancellationRequested();
            retained = true;
            return (await settled.Task.ConfigureAwait(true)).ToolResult();
        }
        finally
        {
            if (!retained)
            {
                var cancelling = Task.CompletedTask;
                if (jobRequested && jobs is not null)
                {
                    try { cancelling = jobs.CancelAsync(started.Id.Value, CancellationToken.None); }
                    catch (Exception error) { Trace.TraceWarning("Shell job cancellation failed ({0}).", error.GetType().Name); }
                }
                // Covers cancellation/progress failure between process creation and job admission.
                // Cleanup failures must not turn a user decline/interruption into a recoverable tool error.
                await RemoveOwnedAsync(runtime, started.Id).ConfigureAwait(true);
                try { await cancelling.ConfigureAwait(true); }
                catch (Exception error) { Trace.TraceWarning("Shell job cancellation failed ({0}).", error.GetType().Name); }
            }
        }
    }

    private static void RequireBackground(IShellToolJobs jobs, ShellJobInfo? info, ShellInfo shell)
    {
        if (!jobs.SupportsBackground || info is null || info.Id != shell.Id.Value || info.NotificationId is not { } notification
            || !notification.IsInitialized() || !notification.Value.StartsWith("msg", StringComparison.Ordinal))
            throw new ToolExecutionException("The host did not commit a recoverable background shell job and notification identity.");
    }

    private static void Watch(ShellRuntime runtime, IShellToolJobs jobs, ShellInfo shell, SessionId session, ShellJobInfo handoff, Task<ShellToolOutput> settled) =>
        runtime.OwnBackground(async ct =>
        {
            var job = await jobs.WaitAsync(handoff.Id, ct).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The background shell job disappeared before completion admission.");
            if (job.Id != handoff.Id || job.Status == ShellJobStatus.Running || job.NotificationId != handoff.NotificationId)
                throw new InvalidOperationException("The background shell job returned an invalid completion identity/state.");
            if (job.Status == ShellJobStatus.Completed && !settled.IsCompletedSuccessfully)
                throw new InvalidOperationException("The completed background shell has no captured result.");
            var output = job.Status == ShellJobStatus.Completed ? await settled.ConfigureAwait(true) : null;
            await jobs.AdmitCompletionAsync(session, handoff.NotificationId!.Value, ShellToolOutput.Notification(shell, job, output), ct).ConfigureAwait(true);
            await jobs.CompleteBackgroundAsync(handoff.NotificationId.Value, ct).ConfigureAwait(true);
        });

    private static async Task RemoveOwnedAsync(ShellRuntime runtime, ShellId id)
    {
        try { await runtime.RemoveAsync(id, CancellationToken.None).ConfigureAwait(true); }
        catch (Exception error) when (error is ShellNotFoundException or ObjectDisposedException) { }
        catch (Exception error) { Trace.TraceWarning("Owned shell cleanup failed ({0}).", error.GetType().Name); }
    }
}
