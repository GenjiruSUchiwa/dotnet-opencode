namespace OpenCode.Cli.Tui.Terminals;
using Transport;

using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using OpenCode.Client;
using OpenCode.Schema;
using OpenTui.Blazor;

/// <summary>UI-dispatcher-owned attachment to one existing persistent PTY. It never creates or deletes a terminal or launches a shell.</summary>
public sealed class PersistentTerminalController : IAsyncDisposable
{
    private abstract record StreamItem;
    private sealed record Output(byte[] Bytes) : StreamItem;
    private sealed record Resize(EmbeddedTerminalSize Size, byte[]? Checkpoint) : StreamItem;
    private sealed record Ready : StreamItem;
    private readonly SessionHttpClient _client;
    private readonly PtyId _ptyId;
    private readonly EmbeddedTerminalState _surface;
    private readonly CancellationTokenSource _lifetime;
    private readonly Queue<StreamItem> _stream = [];
    private readonly Queue<byte[]> _pendingInput = [];
    private readonly string _attachmentId = Guid.NewGuid().ToString();
    private ClientWebSocket? _socket;
    private Task _reader = Task.CompletedTask;
    private Task _writes = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private bool _wantsControl;
    private byte[]? _palette;
    private EmbeddedTerminalSize? _viewport;
    private EmbeddedTerminalSize? _canonical;
    public bool Attached { get; private set; }
    public bool Controller { get; private set; }
    public bool Restored { get; private set; }
    public string? Failure { get; private set; }
    public event Action? Changed;
    public event Action? DisconnectedWhileFocused;

    public PersistentTerminalController(SessionHttpClient client, PtyId ptyId, EmbeddedTerminalState surface, CancellationToken cancellationToken = default)
    {
        _client = client; _ptyId = ptyId; _surface = surface;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        surface.Data += OnData;
    }

    public async Task StartAsync()
    {
        if (_started) throw new InvalidOperationException("A terminal attachment can start only once.");
        _started = true;
        try
        {
            await _surface.Ready.WaitAsync(_lifetime.Token);
            ApplyPalette();
            var snapshot = (await _client.SnapshotPersistentPtyAsync(_ptyId, _lifetime.Token)).Data;
            if (_disposed) return;
            SetCanonical(Size(snapshot.Info.Size.Cols, snapshot.Info.Size.Rows));
            // Native Resize is synchronous: checkpoint replay starts only after
            // the emulator has acknowledged the canonical snapshot size.
            _surface.Write(snapshot.Checkpoint);
            ApplyPalette();
            var socket = await _client.ConnectPersistentPtyAsync(_ptyId, _attachmentId,
                checked((long)snapshot.Info.Output.Tail), takeover: true, framedInput: true, ct: _lifetime.Token);
            if (_disposed) { socket.Dispose(); return; }
            _socket = socket;
            _reader = ReadAsync(socket);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception.Message); }
    }

    public void SetViewport(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        var next = Size(Math.Max(1, width - 2), height);
        if (_viewport == next) return;
        _viewport = next;
        if (Controller && Restored) Interact();
    }
    public void SetPalette(byte[] bytes)
    {
        _palette = bytes.ToArray();
        if (_surface.Ready.IsCompletedSuccessfully && !_disposed) ApplyPalette();
    }
    public void Interact()
    {
        if (!Restored) { _wantsControl = true; return; }
        if (_viewport is { } size) Send(InteractionFrame(size));
    }
    public void Focus() { _surface.RequestFocus(); Interact(); }

    private void OnData(EmbeddedTerminalData data)
    {
        // Persistent terminal replay is emulated on the server as well. Source
        // TerminalPane forwards user input only, not duplicated emulator replies.
        if (_disposed || data.Source != EmbeddedTerminalDataSource.Input) return;
        if (!Restored) { _pendingInput.Enqueue(data.Bytes.ToArray()); return; }
        if (_viewport is { } size) Send(InteractionFrame(size, data.Bytes));
    }
    private void Send(byte[] frame)
    {
        if (!Attached || _socket?.State != WebSocketState.Open || _disposed) return;
        var previous = _writes;
        var socket = _socket;
        _writes = WriteAsync();
        async Task WriteAsync()
        {
            try
            {
                await previous;
                await socket.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { Fail(exception.Message); }
        }
    }

    private async Task ReadAsync(ClientWebSocket socket)
    {
        using var messages = new PipelineWebSocket(socket);
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var result = await messages.ReadAsync(_lifetime.Token);
                if (result.MessageType == WebSocketMessageType.Close) { Fail("Terminal disconnected"); return; }
                if (_disposed) return;
                if (result.MessageType == WebSocketMessageType.Binary) _stream.Enqueue(new Output(result.Data));
                else HandleControl(result.Data);
                ProcessStream();
                // Browser message callbacks return to the renderer between
                // messages. Yield likewise when ReceiveAsync keeps completing
                // synchronously during replay, so the host can paint dirty frames.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Fail(exception.Message); }
    }

    private void HandleControl(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var message = document.RootElement;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type)) return;
        switch (type.GetString())
        {
            case "attached":
                if (!message.TryGetProperty("inputProtocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != 1)
                    throw new InvalidOperationException("Persistent terminal server is out of date; restart OpenCode.");
                if (message.TryGetProperty("info", out var info) && info.TryGetProperty("size", out var size))
                    _stream.Enqueue(new Resize(Size(size.GetProperty("cols").GetDouble(), size.GetProperty("rows").GetDouble()), null));
                Controller = message.TryGetProperty("role", out var role) && role.GetString() == "controller";
                Attached = true;
                Changed?.Invoke();
                break;
            case "resized":
                _stream.Enqueue(new Resize(Size(message.GetProperty("cols").GetDouble(), message.GetProperty("rows").GetDouble()),
                    Convert.FromBase64String(message.GetProperty("checkpoint").GetString() ?? throw new JsonException("Resize checkpoint is missing."))));
                break;
            case "replay_complete": _stream.Enqueue(new Ready()); break;
            case "controller_changed":
                var previous = Controller;
                Controller = message.TryGetProperty("attachmentID", out var attachment) && attachment.ValueKind == JsonValueKind.String && attachment.GetString() == _attachmentId;
                if (Controller && !previous && Restored) Interact();
                Changed?.Invoke();
                break;
        }
    }

    private void ProcessStream()
    {
        if (_disposed || !_surface.Ready.IsCompletedSuccessfully || _canonical is not { } canonical ||
            _surface.Columns != canonical.Columns || _surface.Rows != canonical.Rows) return;
        while (_stream.TryDequeue(out var item))
        {
            switch (item)
            {
                case Output output:
                    using (var bytes = new SequenceBuffer())
                    {
                        bytes.Append(output.Bytes);
                        while (_stream.TryPeek(out var next) && next is Output more) { _stream.Dequeue(); bytes.Append(more.Bytes); }
                        _surface.Write(bytes.ToArray());
                    }
                    break;
                case Resize resize:
                    SetCanonical(resize.Size);
                    if (resize.Checkpoint is { } checkpoint)
                    {
                        _surface.Write("\u001bc"u8);
                        _surface.Write(checkpoint);
                        ApplyPalette();
                    }
                    break;
                case Ready:
                    Restored = true;
                    var hadInput = _pendingInput.Count > 0;
                    while (_pendingInput.TryDequeue(out var input)) OnData(new(input, EmbeddedTerminalDataSource.Input));
                    if (!hadInput && (Controller || _wantsControl)) Interact();
                    _wantsControl = false;
                    Changed?.Invoke();
                    break;
            }
        }
    }
    private void SetCanonical(EmbeddedTerminalSize size) { _canonical = size; _surface.Resize(size.Columns, size.Rows); }
    private void ApplyPalette() { if (_palette is { } palette) _surface.Write(palette); }
    private void Fail(string message)
    {
        if (_disposed || Failure is not null) return;
        Failure = message;
        Attached = false;
        _socket?.Abort();
        if (_surface.Focused) DisconnectedWhileFocused?.Invoke();
        Changed?.Invoke();
    }
    private static EmbeddedTerminalSize Size(double columns, double rows) => double.IsFinite(columns) && double.IsFinite(rows) &&
        columns == Math.Truncate(columns) && rows == Math.Truncate(rows) && columns is >= 1 and <= 65535 && rows is >= 1 and <= 65535
        ? new((int)columns, (int)rows) : throw new JsonException("Terminal dimensions must be integers between 1 and 65535.");
    public static byte[] InteractionFrame(EmbeddedTerminalSize size, ReadOnlyMemory<byte>? data = null)
    {
        Size(size.Columns, size.Rows);
        var frame = new byte[checked(5 + (data?.Length ?? 0))];
        frame[0] = data.HasValue ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1), (ushort)size.Columns);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), (ushort)size.Rows);
        if (data is { } input) input.Span.CopyTo(frame.AsSpan(5));
        return frame;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _surface.Data -= OnData;
        await _lifetime.CancelAsync();
        _socket?.Dispose();
        await Task.WhenAll(_reader, _writes);
        _lifetime.Dispose();
        _stream.Clear();
        _pendingInput.Clear();
    }
}
