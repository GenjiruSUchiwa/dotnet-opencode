namespace OpenCode.Core.Pty;

using Transport;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using OpenCode.Schema;

public sealed record PersistentPtyOptions(string Directory, string? Executable = null, PersistentPtyHandoff? Handoff = null);
public sealed class PersistentPtyUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class PersistentPtyDaemonException(string kind, string message, Exception? inner = null) : IOException(message, inner)
{ public string Kind { get; } = kind; }

internal sealed record PersistentPtyRegistration(
    [property: JsonPropertyName("instance_id"), JsonRequired] string InstanceId,
    [property: JsonPropertyName("pid"), JsonRequired] int Pid,
    [property: JsonPropertyName("protocol"), JsonRequired] int Protocol,
    [property: JsonPropertyName("socket"), JsonRequired] string Socket,
    [property: JsonPropertyName("token"), JsonRequired] string Token);

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PersistentPtyRegistration))]
internal partial class PersistentPtyWireJsonContext : JsonSerializerContext;

/// <summary>Protocol-7 native daemon client. The daemon, not ordinary ConPTY history, owns spooling and screen checkpoints.</summary>
public sealed class PersistentPtyDaemon(PersistentPtyOptions options, TimeProvider? clock = null) : IAsyncDisposable
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal const int ProtocolVersion = 7;
    internal const int MaxFrameBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _startup = new(1, 1);
    private PersistentPtyRegistration? _registration;
    private FrameConnection? _owner;
    private Channel<JsonElement>? _ownerReplies;
    private Task _ownerPump = Task.CompletedTask;
    private bool _closed;
    private bool _inherited;

    public string Directory { get; } = Path.GetFullPath(options.Handoff?.Directory ?? options.Directory);
    public PersistentPtyDeployment Deployment => PersistentPtyAssets.Describe(options.Executable);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (options.Handoff is null || _inherited) return;
        await _startup.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_inherited) return;
            if (options.Handoff.ExpiresAt <= Clock.GetUtcNow().ToUnixTimeMilliseconds())
                throw new PersistentPtyUnavailableException("PTY restart handoff expired");
            var registration = await DiscoverAsync(ct);
            if (registration.InstanceId != options.Handoff.InstanceId)
                throw new PersistentPtyUnavailableException("PTY restart daemon changed");
            await ClaimAsync(registration, options.Handoff.Ticket, ct);
            _inherited = true;
        }
        finally { _startup.Release(); }
    }

    public async Task<JsonElement?> RequestIfRunningAsync(object request, CancellationToken ct = default)
    {
        try { return await RequestAsync(request, ct: ct); }
        catch (PersistentPtyDaemonException error) when (error.Kind == "connect") { return null; }
    }

    public async Task<JsonElement> RequestAsync(object request, bool start = false, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        try { return await AttemptAsync(request, start, ct); }
        catch (PersistentPtyDaemonException error) when (error.Kind is "registration" or "connect")
        {
            // Retry only authentication rejection or failure to connect, never a
            // dispatched mutation whose response was lost.
            await ForgetAsync();
            if (error.Kind == "connect" && !start) throw;
            return await AttemptAsync(request, start, ct);
        }
    }

    private async Task<JsonElement> AttemptAsync(object request, bool start, CancellationToken ct)
    {
        var registration = await ConnectAsync(start, ct);
        return await OneShotAsync(registration, request, ct);
    }

    private async Task<PersistentPtyRegistration> ConnectAsync(bool start, CancellationToken ct)
    {
        await _startup.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_registration is { } current) return current;
            await CloseOwnerAsync();
            PersistentPtyRegistration registration;
            try { registration = await DiscoverAsync(ct); }
            catch (PersistentPtyDaemonException error) when (start && error.Kind == "connect")
            { return await StartAsync(ct); }
            await ClaimAsync(registration, null, ct);
            return registration;
        }
        finally { _startup.Release(); }
    }

    private async Task<PersistentPtyRegistration> DiscoverAsync(CancellationToken ct)
    {
        PersistentPtyRegistration registration;
        try
        {
            registration = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(Directory, "service.json"), ct),
                PersistentPtyWireJsonContext.Default.PersistentPtyRegistration) ?? throw new JsonException("Missing PTY registration.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { throw new PersistentPtyDaemonException("connect", "No persistent PTY daemon is registered in this runtime directory.", error); }
        catch (JsonException error) { throw new PersistentPtyDaemonException("protocol", "Invalid persistent PTY daemon registration.", error); }
        if (registration.Protocol != ProtocolVersion)
            throw new PersistentPtyDaemonException("protocol", $"opencode-pty protocol mismatch: daemon={registration.Protocol}, client={ProtocolVersion}");
        var response = await OneShotAsync(registration, new { op = "ping" }, ct);
        if (Type(response) != "pong" || response.GetProperty("instance_id").GetString() != registration.InstanceId
            || response.GetProperty("pid").GetInt32() != registration.Pid || response.GetProperty("protocol").GetInt32() != ProtocolVersion)
            throw new PersistentPtyDaemonException("protocol", "opencode-pty registration mismatch");
        return registration;
    }

    private async Task ClaimAsync(PersistentPtyRegistration registration, string? ticket, CancellationToken ct)
    {
        using var deadline = Clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var stream = await OpenAsync(registration.Socket, deadline.Token);
        try
        {
            object request = ticket is null ? new { op = "own", instance_id = registration.InstanceId }
                : new { op = "own", instance_id = registration.InstanceId, ticket };
            await WriteAsync(stream, registration.Token, request, deadline.Token);
            var response = Decode(await ReadAsync(stream, deadline.Token));
            Require(response, "owned");
            _owner = stream;
            _registration = registration;
            _ownerReplies = Channel.CreateUnbounded<JsonElement>(new() { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
            _ownerPump = ObserveOwnerAsync(stream, registration, _ownerReplies);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task<PersistentPtyRegistration> StartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PersistentPtyUnavailableException("Automatic persistent PTY daemon launch supports Windows and Linux only.");
        using var input = File.OpenNullHandle();
        var start = new ProcessStartInfo(PersistentPtyAssets.Resolve(options.Executable))
        {
            UseShellExecute = false, StartDetached = true, InheritedHandles = [],
            StandardInputHandle = input, StandardOutputHandle = input, StandardErrorHandle = input
        };
        start.ArgumentList.Add("daemon");
        start.Environment["OPENCODE_PTY_RUNTIME_DIR"] = Directory;
        using var child = SafeProcessHandle.Start(start);
        try
        {
            using var deadline = Clock.CreateLinkedCancellationTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                try
                {
                    var registration = await DiscoverAsync(deadline.Token);
                    await ClaimAsync(registration, null, deadline.Token);
                    return registration;
                }
                catch (PersistentPtyDaemonException error) when (error.Kind == "connect")
                { await Task.Delay(TimeSpan.FromMilliseconds(50), Clock, deadline.Token); }
            }
        }
        catch
        {
            // This is the handle of this attempt's own contender, never a PID
            // taken from a registration or another channel.
            await child.WaitForExitOrKillOnCancellationAsync(new CancellationToken(true));
            throw;
        }
    }

    public async Task<PersistentPtyHandoff?> HandoffAsync(CancellationToken ct = default)
    {
        await _startup.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            var registration = _registration;
            var owner = _owner;
            var replies = _ownerReplies;
            if (registration is null || owner is null || replies is null) return null;
            using var deadline = Clock.CreateLinkedCancellationTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await WriteAsync(owner, registration.Token, new { op = "prepare_handoff" }, deadline.Token);
            var response = await replies.Reader.ReadAsync(deadline.Token);
            Require(response, "handoff");
            return new PersistentPtyHandoff(Directory, registration.InstanceId, response.GetProperty("ticket").GetString()!,
                response.GetProperty("expires_at").GetDouble());
        }
        finally { _startup.Release(); }
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        var response = await RequestIfRunningAsync(new { op = "shutdown" }, ct);
        await ForgetAsync();
        if (response is null) return;
        Require(response.Value, "ok");
        using var deadline = Clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { await DiscoverAsync(deadline.Token); }
            catch (PersistentPtyDaemonException error) when (error.Kind == "connect") { return; }
            await Task.Delay(TimeSpan.FromMilliseconds(50), Clock, deadline.Token);
        }
    }

    internal async Task<(FrameConnection Stream, JsonElement Initial)> SubscribeAsync(long id, long cursor, string attachmentId,
        string role, bool takeover, CancellationToken ct)
    {
        try { return await SubscribeAttemptAsync(id, cursor, attachmentId, role, takeover, ct); }
        catch (PersistentPtyDaemonException error) when (error.Kind == "registration")
        {
            await ForgetAsync();
            return await SubscribeAttemptAsync(id, cursor, attachmentId, role, takeover, ct);
        }
    }

    private async Task<(FrameConnection Stream, JsonElement Initial)> SubscribeAttemptAsync(long id, long cursor, string attachmentId,
        string role, bool takeover, CancellationToken ct)
    {
        var registration = await ConnectAsync(false, ct);
        var stream = await OpenAsync(registration.Socket, ct);
        try
        {
            await WriteAsync(stream, registration.Token, new { op = "subscribe", id, offset = cursor,
                attachment_id = attachmentId, role, takeover }, ct);
            var initial = Decode(await ReadAsync(stream, ct));
            Require(initial, "attached");
            return (stream, initial);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private async Task ForgetAsync()
    {
        await _startup.WaitAsync();
        try
        {
            _registration = null;
            await CloseOwnerAsync();
        }
        finally { _startup.Release(); }
    }

    private async Task ObserveOwnerAsync(FrameConnection stream, PersistentPtyRegistration registration, Channel<JsonElement> replies)
    {
        try
        {
            while (true) await replies.Writer.WriteAsync(Decode(await ReadAsync(stream, CancellationToken.None)));
        }
        catch (Exception error) { replies.Writer.TryComplete(error); }
        finally { Interlocked.CompareExchange(ref _registration, null, registration); }
    }

    private async Task CloseOwnerAsync()
    {
        if (_owner is not null) await _owner.DisposeAsync();
        await _ownerPump;
        _owner = null;
        _ownerReplies = null;
    }

    private async Task<JsonElement> OneShotAsync(PersistentPtyRegistration registration, object request, CancellationToken ct)
    {
        using var deadline = Clock.CreateLinkedCancellationTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var dispatched = false;
        try
        {
            await using var stream = await OpenAsync(registration.Socket, deadline.Token);
            dispatched = true;
            await WriteAsync(stream, registration.Token, request, deadline.Token);
            return Decode(await ReadAsync(stream, deadline.Token));
        }
        catch (PersistentPtyDaemonException) { throw; }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw new PersistentPtyDaemonException(dispatched ? "response" : "connect", "opencode-pty request timed out", error); }
        catch (Exception error) when (error is IOException or SocketException)
        { throw new PersistentPtyDaemonException(dispatched ? "response" : "connect", "opencode-pty local transport failed", error); }
    }

    private static async Task<FrameConnection> OpenAsync(string address, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() && address.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = new NamedPipeClientStream(".", address[9..], PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(ct); return new FrameConnection(pipe); }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), ct); return new FrameConnection(new NetworkStream(socket, ownsSocket: true)); }
        catch { socket.Dispose(); throw; }
    }

    private static async Task WriteAsync(FrameConnection stream, string token, object request, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { token, request });
        if (bytes.Length > MaxFrameBytes) throw new PersistentPtyDaemonException("protocol", "opencode-pty frame too large");
        await stream.WriteAsync(bytes, ct);
    }

    internal static Task<byte[]> ReadAsync(FrameConnection stream, CancellationToken ct) =>
        stream.ReadAsync(MaxFrameBytes, () => new PersistentPtyDaemonException("protocol", "opencode-pty frame too large"), ct);

    internal static JsonElement Decode(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var response = document.RootElement.Clone();
        if (Type(response) == "error")
        {
            var message = response.GetProperty("message").GetString()!;
            throw new PersistentPtyDaemonException(message == "authentication failed" ? "registration" : "protocol", message);
        }
        return response;
    }

    internal static string Type(JsonElement response) => response.GetProperty("type").GetString() ?? throw new JsonException("Missing opencode-pty response type.");
    internal static void Require(JsonElement response, string expected)
    {
        if (Type(response) != expected) throw new PersistentPtyDaemonException("protocol", $"unexpected opencode-pty response: {Type(response)}");
    }

    public async ValueTask DisposeAsync()
    {
        await _startup.WaitAsync();
        try
        {
            if (_closed) return;
            _closed = true;
            await CloseOwnerAsync();
            _registration = null;
        }
        finally { _startup.Release(); }
    }
}
