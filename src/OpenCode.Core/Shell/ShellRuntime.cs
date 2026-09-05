namespace OpenCode.Core.Shell;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenCode.Core.Tools;
using OpenCode.Schema;

/// <summary>Location-owned noninteractive processes and retained output. No Session events, PTY, job recovery, or permission map.</summary>
public sealed partial class ShellRuntime : IAsyncDisposable
{
    private const int ExitedLimit = 25;
    private readonly LocationRef _location;
    private readonly string _directory;
    private readonly Func<ShellCreateInput, CancellationToken, Task<PreparedToolShell>> _prepareUser;
    private readonly Func<CancellationToken, Task> _beforeCreate;
    private readonly Func<SessionId, IReadOnlyDictionary<string, string>?> _environment;
    private readonly Action<OpenCodeEvent> _publish;
    public TimeProvider Clock { get; }
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<ShellId, Entry> _commands = [];
    private readonly List<ShellId> _exited = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _closed;

    public string OutputDirectory => _directory;

    /// <summary>Protect every registered capture, including exited results still observable by jobs/readers.</summary>
    public async Task<IReadOnlyList<string>> OwnedCapturesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_closed, this); return _commands.Values.Select(entry => entry.Info.File).ToArray(); }
        finally { _gate.Release(); }
    }

    private sealed class Entry(ShellInfo info, Process process, SafeFileHandle capture, SafeFileHandle input)
    {
        public ShellInfo Info = info;
        public readonly Process Process = process;
        public readonly SafeFileHandle Capture = capture;
        public readonly SafeFileHandle Input = input;
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource<ShellInfo> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource? Timeout;
        public ShellStatus? RequestedStatus;
        public Task Observer = Task.CompletedTask;
        public bool Removed;
    }

    public ShellRuntime(LocationRef location, string outputDirectory,
        Func<ShellCreateInput, CancellationToken, Task<PreparedToolShell>> prepareUser,
        Func<CancellationToken, Task> beforeCreate,
        Func<SessionId, IReadOnlyDictionary<string, string>?> environment, Action<OpenCodeEvent> publish, TimeProvider? clock = null)
    {
        Clock = clock ?? TimeProvider.System;
        if (location.WorkspaceId is not null) throw new NotSupportedException("Native shell processes support implicit-local Locations only.");
        if (!Path.IsPathFullyQualified(outputDirectory)) throw new ArgumentException("Shell output directory must be absolute.", nameof(outputDirectory));
        _location = location;
        _directory = outputDirectory;
        _prepareUser = prepareUser;
        _beforeCreate = beforeCreate;
        _environment = environment;
        _publish = publish;
    }

    /// <summary>Explicit authenticated-user lane, as protocol shell.create. It is not an LLM tool permission bypass.</summary>
    public async Task<ShellInfo> CreateAsync(ShellCreateInput input, CancellationToken ct = default)
    {
        _ = Duration(input.Timeout);
        var environment = EnvironmentSnapshot(input.Metadata);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _beforeCreate(linked.Token).ConfigureAwait(true);
        return await StartAsync(input, await _prepareUser(input, linked.Token).ConfigureAwait(true), environment, linked.Token).ConfigureAwait(true);
    }

    /// <summary>Tool callers must supply the real Location policy/context. Scan and approve before creating any process/file.</summary>
    public async Task<ShellInfo> CreateToolAsync(ShellCreateInput input, ToolContext context, IToolShellPolicy policy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = Duration(input.Timeout);
        var metadata = (input.Metadata ?? new Dictionary<string, JsonElement>()).ToDictionary(item => item.Key, item => item.Value);
        metadata["sessionID"] = JsonSerializer.SerializeToElement(context.SessionId.Value);
        input = input with { Metadata = metadata };
        var environment = EnvironmentSnapshot(metadata);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _beforeCreate(linked.Token).ConfigureAwait(true);
        var prepared = await policy.PrepareAsync(input.Command, input.Cwd, context, linked.Token).ConfigureAwait(true);
        return await StartAsync(input, prepared, environment, linked.Token).ConfigureAwait(true);
    }

    private IReadOnlyDictionary<string, string> EnvironmentSnapshot(IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata?.TryGetValue("sessionID", out var value) == true && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } id && id.StartsWith("ses", StringComparison.Ordinal)
            && _environment(SessionId.FromExisting(id)) is { } replacement)
            return replacement.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        // Capture host inheritance at preparation time too, rather than reading mutable host
        // environment again after a potentially long tool permission interaction.
        return Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.Ordinal);
    }

    private async Task<ShellInfo> StartAsync(ShellCreateInput input, PreparedToolShell prepared, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        var duration = Duration(input.Timeout);
        if (!Path.IsPathFullyQualified(prepared.Executable) || !Path.IsPathFullyQualified(prepared.WorkingDirectory))
            throw new ArgumentException("Prepared shell executable and working directory must be absolute.", nameof(prepared));
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            // Revalidate after approval, immediately before process creation.
            if (!Directory.Exists(prepared.WorkingDirectory)) throw new DirectoryNotFoundException("Shell working directory no longer exists.");
            if (!File.Exists(prepared.Executable)) throw new FileNotFoundException("Selected shell no longer exists.");
            Directory.CreateDirectory(_directory);
            var id = ShellId.Create();
            var file = Path.Combine(_directory, id.Value + ".out");
            var capture = File.OpenHandle(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            SafeFileHandle? stdin = null;
            Process? process = null;
            var started = false;
            var adopted = false;
            try
            {
                stdin = File.OpenNullHandle();
                var start = new ProcessStartInfo(prepared.Executable)
                {
                    WorkingDirectory = prepared.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                    StartDetached = false, InheritedHandles = [], StandardInputHandle = stdin,
                    StandardOutputHandle = capture, StandardErrorHandle = capture
                };
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) start.KillOnParentExit = true;
                if (environment is not null)
                {
                    start.Environment.Clear();
                    foreach (var item in environment) start.Environment[item.Key] = item.Value;
                }
                start.Environment["TERM"] = "xterm-256color";
                start.Environment["OPENCODE_TERMINAL"] = "1";
                foreach (var argument in prepared.Arguments) start.ArgumentList.Add(argument);
                process = new Process { StartInfo = start };
                ct.ThrowIfCancellationRequested();
                started = process.Start();
                if (!started) throw new IOException("Shell process did not start.");
                var info = new ShellInfo(id, ShellStatus.Running, input.Command, prepared.WorkingDirectory, prepared.Executable, file,
                    new ShellTime(Clock.GetUtcNow().ToUnixTimeMilliseconds()),
                    (input.Metadata ?? new Dictionary<string, JsonElement>()).ToDictionary(item => item.Key, item => item.Value.Clone()), process.Id);
                var entry = new Entry(info, process, capture, stdin);
                _commands.Add(id, entry);
                _publish(ShellEventDefinitions.Created.Create(EventId.Create(), Clock.GetUtcNow().ToUnixTimeMilliseconds(), new ShellInfoEventData(info), _location));
                SetTimeout(entry, duration);
                entry.Observer = ObserveAsync(entry);
                adopted = true;
                return info;
            }
            catch
            {
                _commands.Remove(id);
                if (started && process is not null)
                {
                    using var stop = new CancellationTokenSource();
                    var wait = process.SafeHandle.WaitForExitOrKillOnCancellationAsync(stop.Token);
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    finally { await stop.CancelAsync().ConfigureAwait(true); await wait.ConfigureAwait(true); }
                }
                throw;
            }
            finally
            {
                if (!adopted)
                {
                    process?.Dispose(); capture.Dispose(); stdin?.Dispose();
                    File.Delete(file);
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ShellInfo>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_commands.Values.Any(entry => entry.Done.Task.IsFaulted)) throw new IOException("A shell lifecycle could not be observed; remove that command before using its status.");
            return _commands.Values.Where(entry => entry.Info.Status == ShellStatus.Running).Select(entry => entry.Info).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ShellInfo> GetAsync(ShellId id, CancellationToken ct = default) => (await RequireAsync(id, ct).ConfigureAwait(false)).Info;
    public async Task<ShellInfo> WaitAsync(ShellId id, CancellationToken ct = default) => await (await RequireAsync(id, ct).ConfigureAwait(false)).Done.Task.WaitAsync(ct).ConfigureAwait(false);

    public async Task<ShellInfo> TimeoutAsync(ShellId id, double milliseconds, CancellationToken ct = default)
    {
        var duration = Duration(milliseconds);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var entry = Require(id);
            if (entry.Info.Status == ShellStatus.Running && entry.RequestedStatus is null) SetTimeout(entry, duration);
            return entry.Info;
        }
        finally { _gate.Release(); }
    }

    public async Task<ShellOutput> OutputAsync(ShellId id, ShellOutputInput? input = null, CancellationToken ct = default)
    {
        var entry = await RequireAsync(id, ct).ConfigureAwait(false);
        try
        {
            var stream = new FileStream(entry.Info.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            await using var streamLifetime = stream.ConfigureAwait(false);
            var size = stream.Length;
            var cursor = input?.Cursor ?? 0;
            if (cursor >= size) return new("", size, size, false);
            var limit = input?.Limit ?? 65_536;
            if (limit > int.MaxValue) throw new NotSupportedException("A single shell output page must fit in a native byte buffer.");
            var start = checked((long)cursor);
            var buffer = new byte[(int)Math.Min(limit, size - start)];
            stream.Position = start;
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false);
            return new(Encoding.UTF8.GetString(buffer, 0, read), start + read, size, false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new ShellOutputUnavailableException(id, error); }
    }

    public async Task<ShellResult> ResultAsync(ShellInfo started, int maximumBytes = 50 * 1024, int maximumLines = 2000, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLines, 1);
        ShellInfo info;
        try { info = await WaitAsync(started.Id, ct).ConfigureAwait(true); }
        catch (ShellNotFoundException) { info = started with { Status = ShellStatus.Killed, Time = started.Time with { Completed = Clock.GetUtcNow().ToUnixTimeMilliseconds() } }; }
        try
        {
            var latest = await OutputAsync(started.Id, new(Cursor: 9_007_199_254_740_991), ct).ConfigureAwait(true);
            var page = await OutputAsync(started.Id, new(Cursor: Math.Max(0, latest.Size - maximumBytes), Limit: maximumBytes), ct).ConfigureAwait(true);
            var lines = page.Output.Split('\n');
            var count = lines.Length - (page.Output.EndsWith('\n') ? 1 : 0);
            var truncated = latest.Size > maximumBytes || count > maximumLines;
            var text = count > maximumLines ? string.Join('\n', lines.Take(count).TakeLast(maximumLines)) : page.Output;
            return new(info, new((text.Length == 0 ? "(no output)" : text) + (truncated ? $"\n\n[output truncated; full output saved to: {info.File}]" : ""), truncated));
        }
        catch (Exception error) when (error is ShellNotFoundException or ShellOutputUnavailableException) { return new(info, null); }
    }

    /// <summary>Terminate and remove the owned command/capture. No separate invented HTTP interrupt route.</summary>
    public async Task RemoveAsync(ShellId id, CancellationToken ct = default)
    {
        Entry entry;
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            entry = Require(id, allowFailure: true);
            entry.Removed = true;
            CancelTimeout(entry);
            _commands.Remove(id);
            _exited.Remove(id);
            entry.Done.TrySetException(new ShellNotFoundException(id));
            _ = entry.Done.Task.Exception;
        }
        finally { _gate.Release(); }
        try { await StopAsync(entry).ConfigureAwait(true); }
        finally
        {
            await entry.Observer.ConfigureAwait(true);
            File.Delete(entry.Info.File);
            _publish(ShellEventDefinitions.Deleted.Create(EventId.Create(), Clock.GetUtcNow().ToUnixTimeMilliseconds(), new ShellDeletedEventData(id), _location));
        }
    }

    private async Task ObserveAsync(Entry entry)
    {
        await Task.Yield();
        try
        {
            var result = await entry.Process.SafeHandle.WaitForExitOrKillOnCancellationAsync(entry.Stop.Token).ConfigureAwait(true);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                if (_closed || entry.Removed) return;
                CancelTimeout(entry);
                entry.Info = entry.Info with
                {
                    Status = entry.RequestedStatus ?? (result.Canceled ? ShellStatus.Killed : ShellStatus.Exited),
                    Exit = entry.RequestedStatus is null && !result.Canceled ? result.ExitCode : null,
                    Time = entry.Info.Time with { Completed = Clock.GetUtcNow().ToUnixTimeMilliseconds() }
                };
                entry.Done.TrySetResult(entry.Info);
                _publish(ShellEventDefinitions.Exited.Create(EventId.Create(), Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                    new ShellExitedEventData(entry.Info.Id, entry.Info.Status, entry.Info.Exit), _location));
                _exited.Add(entry.Info.Id);
                while (_exited.Count > ExitedLimit)
                {
                    var id = _exited[0];
                    _exited.RemoveAt(0);
                    if (!_commands.Remove(id, out var old)) continue;
                    File.Delete(old.Info.File);
                    _publish(ShellEventDefinitions.Deleted.Create(EventId.Create(), Clock.GetUtcNow().ToUnixTimeMilliseconds(), new ShellDeletedEventData(id), _location));
                }
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) { entry.Done.TrySetException(error); _ = entry.Done.Task.Exception; }
        finally
        {
            try
            {
                if (entry.Done.Task.IsFaulted && !entry.Process.HasExited)
                {
                    await StopAsync(entry).ConfigureAwait(true);
                    await entry.Process.SafeHandle.WaitForExitOrKillOnCancellationAsync(entry.Stop.Token).ConfigureAwait(true);
                }
            }
            finally { entry.Process.Dispose(); entry.Capture.Dispose(); entry.Input.Dispose(); }
        }
    }

    private void SetTimeout(Entry entry, int duration)
    {
        CancelTimeout(entry);
        entry.Timeout = duration == 0 ? null : new CancellationTokenSource();
        if (entry.Timeout is { } timeout) _ = ExpireAsync(entry, duration, timeout);
    }

    private async Task ExpireAsync(Entry entry, int duration, CancellationTokenSource timeout)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(duration), Clock, timeout.Token).ConfigureAwait(true);
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(true);
            try
            {
                if (_closed || entry.Removed || entry.Info.Status != ShellStatus.Running || entry.Timeout != timeout) return;
                entry.RequestedStatus = ShellStatus.Timeout;
            }
            finally { _gate.Release(); }
            await StopAsync(entry).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (Exception error) { Trace.TraceWarning("Shell timeout cleanup failed ({0}).", error.GetType().Name); }
    }

    // This token is private to Delay/gate waiting. Cancel synchronously while holding
    // the lifecycle gate, before replacing its timeout or publishing terminal state.
    private static void CancelTimeout(Entry entry) => entry.Timeout?.Cancel();

    private static async Task StopAsync(Entry entry)
    {
        if (entry.Observer.IsCompleted) return;
        try { if (!entry.Process.HasExited) entry.Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* The observer may have reaped the owned process concurrently. */ }
        finally { await entry.Stop.CancelAsync().ConfigureAwait(false); }
    }

    private async Task<Entry> RequireAsync(ShellId id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return Require(id); }
        finally { _gate.Release(); }
    }
    private Entry Require(ShellId id, bool allowFailure = false)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var entry = _commands.GetValueOrDefault(id) ?? throw new ShellNotFoundException(id);
        if (!allowFailure && entry.Done.Task.IsFaulted) throw new IOException("The shell lifecycle is unavailable.", entry.Done.Task.Exception);
        return entry;
    }
    private static int Duration(double value) => value is >= 0 and <= int.MaxValue && double.IsFinite(value) && value == Math.Truncate(value)
        ? (int)value : throw new NotSupportedException("Native shell timeouts must be integer milliseconds from 0 through 2147483647; zero disables the timeout.");

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            if (_closed) return;
            _closed = true;
            entries = _commands.Values.ToArray();
            _commands.Clear(); _exited.Clear();
            foreach (var entry in entries) { CancelTimeout(entry); entry.Done.TrySetCanceled(CancellationToken.None); }
        }
        finally { _gate.Release(); }
        await _shutdown.CancelAsync().ConfigureAwait(true);
        await Task.WhenAll(entries.Select(async entry =>
        {
            try { await StopAsync(entry).ConfigureAwait(true); }
            finally { await entry.Observer.ConfigureAwait(true); }
        })).ConfigureAwait(true);
        Task[] background;
        lock (_backgroundGate) background = _background.ToArray();
        await Task.WhenAll(background).ConfigureAwait(true);
        // Preserve capture files for the host's seven-day retention sweep; shutdown is not an exit event.
    }
}
