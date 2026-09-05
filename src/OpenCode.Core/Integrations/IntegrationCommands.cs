namespace OpenCode.Core.Integrations;
using Transport;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>Explicit native registrations, not declarations from an unloaded JavaScript plugin.</summary>
public sealed record IntegrationCommandRegistration(IntegrationRef Integration, IntegrationCommandMethod Method);

public interface IIntegrationCommandSource
{
    IReadOnlyList<IntegrationCommandRegistration> Methods(LocationInfo location);
}

public sealed partial class IntegrationRuntime
{
    private readonly Dictionary<IntegrationAttemptId, CommandEntry> _commands = [];
    private readonly HashSet<Task> _commandWorkers = [];

    private sealed class CommandEntry(string integration, IntegrationCommandAttempt info, CancellationToken shutdown)
    {
        internal string Integration { get; } = integration;
        internal IntegrationCommandAttempt Info { get; } = info;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        internal IntegrationCommandAttemptStatus Status = new IntegrationPendingCommandStatus(info.Time);
        internal bool Persisting;
        internal DateTimeOffset? RemoveAt;
        internal Task Worker = Task.CompletedTask;
    }

    public Task<IntegrationCommandAttempt> ConnectCommandAsync(string integration, IntegrationCommandConnectPayload input, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var definition = RequireDefinition(integration);
            var method = definition.Methods.OfType<IntegrationCommandMethod>().FirstOrDefault(item => item.Id == input.MethodId.Value);
            if (method is null || method.Command.Count == 0 || string.IsNullOrEmpty(method.Command[0]))
                throw new IntegrationAuthorizationException("Command method not found.");
            var now = credentials.Clock.GetUtcNow();
            var info = new IntegrationCommandAttempt(IntegrationAttemptId.Create(), new(now.ToUnixTimeMilliseconds(), now.AddMinutes(10).ToUnixTimeMilliseconds()));
            var entry = new CommandEntry(integration, info, _shutdown.Token);
            _commands.Add(info.AttemptId, entry);
            _scrubber ??= ScrubAsync();
            entry.Worker = RunCommandAsync(entry, method.Command.ToArray(), input.Label, definition.Reference.Name);
            _commandWorkers.RemoveWhere(worker => worker.IsCompleted);
            _commandWorkers.Add(entry.Worker);
            return Task.FromResult(info);
        }
    }

    public IntegrationCommandAttemptStatus CommandStatus(string integration, IntegrationAttemptId attempt)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _commands.TryGetValue(attempt, out var entry) && entry.Integration == integration
                ? entry.Status : throw new IntegrationAuthorizationException("Command attempt not found.");
        }
    }

    public async Task CancelCommandAsync(string integration, IntegrationAttemptId attempt)
    {
        CommandEntry? entry;
        lock (_gate)
        {
            if (!_commands.TryGetValue(attempt, out entry) || entry.Integration != integration
                || entry.Status is not IntegrationPendingCommandStatus || entry.Persisting) return;
            _commands.Remove(attempt);
        }
        await entry.Cancellation.CancelAsync().ConfigureAwait(true);
        await entry.Worker.ConfigureAwait(true);
        entry.Cancellation.Dispose();
    }

    private async Task RunCommandAsync(CommandEntry entry, string[] command, string? label, string name)
    {
        await Task.Yield();
        try
        {
            var key = await CommandCredentialAsync(command, chunk =>
            {
                lock (_gate)
                    if (entry.Status is IntegrationPendingCommandStatus pending)
                        entry.Status = new IntegrationPendingCommandStatus(entry.Info.Time, (pending.Message ?? "") + chunk);
            }, entry.Cancellation.Token).ConfigureAwait(true);
            lock (_gate)
            {
                if (!_commands.ContainsKey(entry.Info.AttemptId) || entry.Status is not IntegrationPendingCommandStatus
                    || entry.Cancellation.IsCancellationRequested) return;
                entry.Persisting = true;
            }
            // The cancellation/expiry boundary is before persistence. Once claimed,
            // finish the shared-store commit and publish only secret-free notifications.
            var selectedLabel = label ?? await UniqueLabelAsync(credentials, entry.Integration, name, CancellationToken.None).ConfigureAwait(true);
            var mutation = await credentials.CreateAsync(entry.Integration,
                JsonSerializer.SerializeToElement<CredentialValue>(new CredentialKey(key), OpenCodeJsonContext.Default.CredentialValue),
                selectedLabel, CancellationToken.None).ConfigureAwait(true);
            await publish(mutation, CancellationToken.None).ConfigureAwait(true);
            lock (_gate) entry.Status = new IntegrationCompleteCommandStatus(entry.Info.Time);
        }
        catch (Exception error)
        {
            lock (_gate)
                if (entry.Status is IntegrationPendingCommandStatus)
                    entry.Status = new IntegrationFailedCommandStatus(entry.Info.Time,
                        error is IntegrationAuthorizationException ? error.Message : "Authentication command failed");
        }
        finally { lock (_gate) entry.RemoveAt = credentials.Clock.GetUtcNow().AddMinutes(1); }
    }

    private async Task ScrubCommandsAsync()
    {
        CommandEntry[] expired;
        lock (_gate)
        {
            var now = credentials.Clock.GetUtcNow();
            foreach (var id in _commands.Where(item => item.Value.RemoveAt <= now && item.Value.Worker.IsCompleted).Select(item => item.Key).ToArray())
            {
                _commands[id].Cancellation.Dispose();
                _commands.Remove(id);
            }
            expired = _commands.Values.Where(entry => entry.Status is IntegrationPendingCommandStatus && !entry.Persisting
                && entry.Info.Time.Expires <= now.ToUnixTimeMilliseconds()).ToArray();
            foreach (var entry in expired) entry.Status = new IntegrationExpiredCommandStatus(entry.Info.Time);
        }
        foreach (var entry in expired) await entry.Cancellation.CancelAsync().ConfigureAwait(true);
    }

    private async Task DisposeCommandsAsync()
    {
        CommandEntry[] entries;
        Task[] workers;
        lock (_gate) { entries = _commands.Values.ToArray(); workers = _commandWorkers.ToArray(); }
        await Task.WhenAll(workers).ConfigureAwait(true);
        lock (_gate)
        {
            foreach (var entry in entries) entry.Cancellation.Dispose();
            _commands.Clear();
            _commandWorkers.Clear();
        }
    }

    private static async Task<string> CommandCredentialAsync(string[] command, Action<string> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false, CreateNoWindow = true, StartDetached = false, InheritedHandles = [],
            StandardInputHandle = input, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
        foreach (var argument in command.Skip(1)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        using var stopping = new CancellationTokenSource();
        var exited = process.SafeHandle.WaitForExitOrKillOnCancellationAsync(stopping.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(readers.Token);
        var stderr = ReadErrorAsync();
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(ct).ConfigureAwait(true);
            var result = await exited.WaitAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            var errorOutput = await stderr.ConfigureAwait(true);
            if (result.ExitCode != 0)
                throw new IntegrationAuthorizationException(string.IsNullOrWhiteSpace(errorOutput)
                    ? $"Authentication command exited {result.ExitCode}" : errorOutput.Trim());
            var key = (await stdout.ConfigureAwait(true)).Trim();
            if (key.Length == 0) throw new IntegrationAuthorizationException("Authentication command returned no credential");
            return key;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            finally
            {
                await stopping.CancelAsync().ConfigureAwait(true);
                await readers.CancelAsync().ConfigureAwait(true);
                await exited.ConfigureAwait(true);
                await ((Task)Task.WhenAll(stdout, stderr)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        async Task<string> ReadErrorAsync()
        {
            var text = new StringBuilder();
            await foreach (var chunk in PipelineText.ChunksAsync(process.StandardError.BaseStream, process.StandardError.CurrentEncoding,
                cancellationToken: readers.Token).ConfigureAwait(true))
            {
                text.Append(chunk);
                output(chunk);
            }
            return text.ToString();
        }
    }
}
