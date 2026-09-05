namespace OpenCode.Core.Pty;
using Transport;

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenCode.Schema;

public abstract record PersistentPtyStreamEvent;
public sealed record PersistentPtyOutputEvent(long Start, long End, byte[] Data) : PersistentPtyStreamEvent;
public sealed record PersistentPtyResizeEvent(double Cols, double Rows, long Generation, byte[] Checkpoint) : PersistentPtyStreamEvent;
public sealed record PersistentPtyExitEvent(double? ExitCode, long FinalOffset) : PersistentPtyStreamEvent;
public sealed record PersistentPtyControllerEvent(string? AttachmentId, long Generation) : PersistentPtyStreamEvent;
public sealed record PersistentPtyTitleEvent(string Title) : PersistentPtyStreamEvent;
public sealed record PersistentPtyForegroundEvent(string? Process) : PersistentPtyStreamEvent;
public sealed record PersistentPtyReplay(long RequestedOffset, long AvailableOffset, long EndOffset, bool Truncated, byte[] Data);

/// <summary>Global, daemon-backed persistent PTYs. Unlike ordinary PTYs, Location teardown does not own these terminals.</summary>
public sealed class PersistentPtyService(PersistentPtyDaemon daemon, Func<string> shell, Action<OpenCodeEvent> publish) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, PtyId> _current = [];
    private readonly HashSet<PtyId> _removing = [];
    private readonly HashSet<Task> _tasks = [];
    private readonly CancellationTokenSource _shutdown = new();

    public Task InitializeAsync(CancellationToken ct = default) => daemon.InitializeAsync(ct);

    public async Task<IReadOnlyList<PersistentPtyInfo>> ListAsync(SessionId? session = null, CancellationToken ct = default)
    {
        var response = await daemon.RequestIfRunningAsync(new { op = "list" }, ct).ConfigureAwait(true);
        if (response is null) return [];
        PersistentPtyDaemon.Require(response.Value, "terminals");
        return response.Value.GetProperty("terminals").EnumerateArray().Select(Info)
            .Where(info => session is null || info.SessionId == session).ToArray();
    }

    public async Task<PersistentPtyInfo> GetAsync(PtyId id, CancellationToken ct = default) =>
        (await ListAsync(ct: ct).ConfigureAwait(true)).FirstOrDefault(info => info.Id == id) ?? throw new PtyNotFoundException(id);

    public async Task<PersistentPtyInfo> CreateAsync(SessionId session, PersistentPtyCreateInput input, CancellationToken ct = default)
    {
        var response = await daemon.RequestAsync(new
        {
            op = "create", program = input.Command ?? shell(), args = input.Args,
            cwd = input.Cwd ?? Path.GetPathRoot(Path.GetFullPath("/")), title = input.Title, group_id = session.Value,
            env = input.Env, cols = input.Size?.Cols ?? 80, rows = input.Size?.Rows ?? 24
        }, start: true, ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(response, "created");
        var terminal = Info(response.GetProperty("terminal"));
        publish(PersistentPtyEventDefinitions.Added.Create(EventId.Create(), daemon.Clock.GetUtcNow().ToUnixTimeMilliseconds(), new(session, terminal)));
        return terminal;
    }

    public async Task WriteAsync(PtyId id, string data, string? attachmentId = null, CancellationToken ct = default)
    {
        await GetAsync(id, ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(await daemon.RequestAsync(new { op = "write", id = Number(id), attachment_id = attachmentId,
            data_base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(data)) }, ct: ct).ConfigureAwait(true), "ok");
    }

    public async Task ResizeAsync(PtyId id, TerminalSize size, string? attachmentId = null, CancellationToken ct = default)
    {
        var terminal = await GetAsync(id, ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(await daemon.RequestAsync(new { op = "resize", id = Number(id), attachment_id = attachmentId,
            cols = size.Cols, rows = size.Rows }, ct: ct).ConfigureAwait(true), "ok");
        lock (_gate) _current[terminal.SessionId] = id;
    }

    public async Task InputAsync(PtyId id, string attachmentId, TerminalSize size, ReadOnlyMemory<byte> data,
        bool control = false, CancellationToken ct = default)
    {
        var terminal = await GetAsync(id, ct).ConfigureAwait(true);
        var response = control
            ? await daemon.RequestAsync(new { op = "control", id = Number(id), attachment_id = attachmentId, cols = size.Cols, rows = size.Rows }, ct: ct).ConfigureAwait(true)
            : await daemon.RequestAsync(new { op = "input", id = Number(id), attachment_id = attachmentId, cols = size.Cols, rows = size.Rows,
                data_base64 = Convert.ToBase64String(data.Span) }, ct: ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(response, "ok");
        lock (_gate) _current[terminal.SessionId] = id;
    }

    public async Task<PersistentPtySnapshot> SnapshotAsync(PtyId id, CancellationToken ct = default)
    {
        await GetAsync(id, ct).ConfigureAwait(true);
        var response = await daemon.RequestAsync(new { op = "snapshot", id = Number(id) }, ct: ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(response, "snapshot");
        return new(Info(response.GetProperty("terminal")), response.GetProperty("text").GetString()!,
            Convert.FromBase64String(response.GetProperty("checkpoint_base64").GetString()!),
            new(response.GetProperty("cursor_x").GetDouble(), response.GetProperty("cursor_y").GetDouble()));
    }

    public async Task<PersistentPtyReadResult?> ReadAsync(SessionId session, int? lines = null, CancellationToken ct = default)
    {
        if (lines is < 1 or > 65535) throw new PersistentPtyUnavailableException("lines must be an integer between 1 and 65535");
        PtyId id;
        lock (_gate) if (!_current.TryGetValue(session, out id)) return null;
        PersistentPtyInfo terminal;
        try { terminal = await GetAsync(id, ct).ConfigureAwait(true); }
        catch (PtyNotFoundException)
        {
            lock (_gate) if (_current.TryGetValue(session, out var current) && current == id) _current.Remove(session);
            return null;
        }
        if (terminal.SessionId != session)
        {
            lock (_gate) if (_current.TryGetValue(session, out var current) && current == id) _current.Remove(session);
            return null;
        }
        object request = lines is null ? new { op = "read_rows", id = Number(id) } : new { op = "read_rows", id = Number(id), rows = lines.Value };
        var response = await daemon.RequestAsync(request, ct: ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(response, "rows");
        var info = Info(response.GetProperty("terminal"));
        return new(info.Id, info.Title, info.Cwd, info.ForegroundProcess,
            new(string.Join("\n", response.GetProperty("lines").EnumerateArray().Select(line => line.GetString() ?? throw new JsonException("Invalid persistent terminal row."))), info.Size.Cols, info.Size.Rows,
                new(response.GetProperty("cursor_x").GetDouble(), response.GetProperty("cursor_y").GetDouble())));
    }

    public async Task RemoveAsync(PtyId id, CancellationToken ct = default)
    {
        var terminal = await GetAsync(id, ct).ConfigureAwait(true);
        PersistentPtyDaemon.Require(await daemon.RequestAsync(new { op = "terminate", id = Number(id) }, ct: ct).ConfigureAwait(true), "ok");
        lock (_gate) if (_current.TryGetValue(terminal.SessionId, out var current) && current == id) _current.Remove(terminal.SessionId);
        publish(PersistentPtyEventDefinitions.Removed.Create(EventId.Create(), daemon.Clock.GetUtcNow().ToUnixTimeMilliseconds(), new(terminal.SessionId, id)));
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await daemon.ShutdownAsync(ct).ConfigureAwait(true);
        lock (_gate) _current.Clear();
    }
    public Task<PersistentPtyHandoff?> HandoffAsync(CancellationToken ct = default) => daemon.HandoffAsync(ct);

    public async Task<PersistentPtyAttachment> AttachAsync(PtyId id, long cursor, string attachmentId, string role,
        bool takeover, Action<PersistentPtyStreamEvent> onEvent, Action onEnd, CancellationToken ct = default)
    {
        await GetAsync(id, ct).ConfigureAwait(true);
        var connection = await daemon.SubscribeAsync(Number(id), cursor, attachmentId, role, takeover, ct).ConfigureAwait(true);
        try
        {
            var attachment = new PersistentPtyAttachment(connection.Stream, connection.Initial, change =>
            {
                if (change is PersistentPtyExitEvent) RemoveVisibleExit(id);
                onEvent(change);
            }, onEnd, _shutdown.Token);
            if (attachment.Role == "controller") lock (_gate) _current[attachment.Info.SessionId] = id;
            return attachment;
        }
        catch { await connection.Stream.DisposeAsync().ConfigureAwait(true); throw; }
    }

    private void RemoveVisibleExit(PtyId id)
    {
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested || !_removing.Add(id)) return;
            var task = RemoveVisibleAsync(id);
            _tasks.Add(task);
            _ = task.ContinueWith(completed => { lock (_gate) _tasks.Remove(completed); }, TaskScheduler.Default);
        }
    }

    private async Task RemoveVisibleAsync(PtyId id)
    {
        await Task.Yield();
        try { await RemoveAsync(id, _shutdown.Token).ConfigureAwait(true); }
        catch (PtyNotFoundException) { }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Could not remove exited persistent terminal {0}: {1}", id, error.Message); }
        finally { lock (_gate) _removing.Remove(id); }
    }

    internal static long Number(PtyId id) => id.Value.StartsWith("pty_persistent_", StringComparison.Ordinal)
        && long.TryParse(id.Value[15..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 9007199254740991 ? number
        : throw new PersistentPtyUnavailableException($"invalid persistent PTY ID: {id}");

    internal static PersistentPtyInfo Info(JsonElement value)
    {
        var command = value.GetProperty("command").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var lifecycle = value.GetProperty("lifecycle");
        var status = lifecycle.GetProperty("status").GetString();
        if (status is not ("running" or "exited" or "failed")) throw new JsonException("Invalid persistent PTY lifecycle.");
        var exit = status == "exited" && lifecycle.TryGetProperty("exit_code", out var code) && code.ValueKind != JsonValueKind.Null ? code.GetDouble() : (double?)null;
        return new(PtyId.FromExisting("pty_persistent_" + value.GetProperty("id").GetInt64()), SessionId.FromExisting(value.GetProperty("group_id").GetString()!),
            value.GetProperty("title").GetString()!, command.FirstOrDefault() ?? "", command.Skip(1).ToArray(), value.GetProperty("cwd").GetString()!,
            status == "running" ? PtyStatus.Running : PtyStatus.Exited, value.GetProperty("pid").ValueKind == JsonValueKind.Null ? 0 : value.GetProperty("pid").GetDouble(),
            new(value.GetProperty("cols").GetDouble(), value.GetProperty("rows").GetDouble()),
            new(value.GetProperty("output_head").GetDouble(), value.GetProperty("output_tail").GetDouble()),
            value.GetProperty("foreground_process").GetString(), exit);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(true);
        Task[] tasks;
        lock (_gate) tasks = _tasks.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(true);
        await daemon.DisposeAsync().ConfigureAwait(true);
    }
}

public sealed class PersistentPtyAttachment : IAsyncDisposable
{
    private readonly FrameConnection _stream;
    private readonly Action<PersistentPtyStreamEvent> _onEvent;
    private readonly Action _onEnd;
    private readonly CancellationTokenSource _lifetime;
    private Task _pump = Task.CompletedTask;
    private int _active;
    private int _disposed;
    public PersistentPtyInfo Info { get; }
    public string Role { get; }
    public long Generation { get; }
    public PersistentPtyReplay Replay { get; }

    internal PersistentPtyAttachment(FrameConnection stream, JsonElement initial, Action<PersistentPtyStreamEvent> onEvent, Action onEnd, CancellationToken ct)
    {
        _stream = stream; _onEvent = onEvent; _onEnd = onEnd;
        Info = PersistentPtyService.Info(initial.GetProperty("terminal"));
        Role = initial.GetProperty("role").GetString()!;
        if (Role is not ("controller" or "observer")) throw new JsonException("Invalid PTY attachment role.");
        Generation = initial.GetProperty("generation").GetInt64();
        Replay = new(initial.GetProperty("requested_offset").GetInt64(), initial.GetProperty("available_offset").GetInt64(),
            initial.GetProperty("end_offset").GetInt64(), initial.GetProperty("truncated").GetBoolean(),
            Convert.FromBase64String(initial.GetProperty("replay_base64").GetString()!));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public void Activate()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _active, 1) != 0) return;
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var frame = await PersistentPtyDaemon.ReadAsync(_stream, _lifetime.Token).ConfigureAwait(true);
                if (frame.Length > 0 && frame[0] == 0)
                {
                    if (frame.Length < 17) throw new JsonException("invalid opencode-pty output frame");
                    _onEvent(new PersistentPtyOutputEvent(checked((long)BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(1))),
                        checked((long)BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(9))), frame[17..]));
                    continue;
                }
                var response = PersistentPtyDaemon.Decode(frame);
                PersistentPtyStreamEvent? change = PersistentPtyDaemon.Type(response) switch
                {
                    "resized" => new PersistentPtyResizeEvent(response.GetProperty("cols").GetDouble(), response.GetProperty("rows").GetDouble(),
                        response.GetProperty("generation").GetInt64(), Convert.FromBase64String(response.GetProperty("checkpoint_base64").GetString()!)),
                    "controller_changed" => new PersistentPtyControllerEvent(response.GetProperty("attachment_id").GetString(), response.GetProperty("generation").GetInt64()),
                    "title_changed" => new PersistentPtyTitleEvent(response.GetProperty("title").GetString()!),
                    "foreground_process_changed" => new PersistentPtyForegroundEvent(response.GetProperty("process").GetString()),
                    "exited" => new PersistentPtyExitEvent(response.GetProperty("exit_code").ValueKind == JsonValueKind.Null ? null : response.GetProperty("exit_code").GetDouble(),
                        response.GetProperty("final_offset").GetInt64()),
                    _ => null
                };
                if (change is not null) _onEvent(change);
                if (change is PersistentPtyExitEvent) return;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Persistent PTY attachment ended: {0}", error.Message); }
        finally { if (Volatile.Read(ref _disposed) == 0) _onEnd(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetime.CancelAsync().ConfigureAwait(true);
        await _stream.DisposeAsync().ConfigureAwait(true);
        await _pump.ConfigureAwait(true);
        _lifetime.Dispose();
    }
}
