namespace OpenCode.Client;
using Transport;

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public abstract record PtySocketEvent;
public sealed record PtyTextEvent(string Text) : PtySocketEvent;
public sealed record PtyCursorEvent(long Cursor) : PtySocketEvent;
public sealed record PtyClosedEvent(int? Code, string? Reason) : PtySocketEvent;
internal sealed record PtyCursorFrame([property: JsonPropertyName("cursor"), JsonRequired] long Cursor);

/// <summary>Network-only PTY protocol adapter. Disposing an attachment does not delete its server-owned terminal.</summary>
public sealed class PtyConnection : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _abort;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private int _reading;
    private int _disposed;

    internal PtyConnection(ClientWebSocket socket, CancellationToken lifetime)
    {
        _socket = socket;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _abort = _lifetime.Token.Register(socket.Abort);
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _sending.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, cancellation.Token).ConfigureAwait(false); }
        finally { _sending.Release(); }
    }

    public async IAsyncEnumerable<PtySocketEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("A PTY connection supports one output reader.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var messages = new PipelineWebSocket(_socket);
        var utf8 = new UTF8Encoding(false, true);
        while (true)
        {
            var result = await messages.ReadAsync(cancellation.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _sending.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token).ConfigureAwait(false); }
                finally { _sending.Release(); }
                yield return new PtyClosedEvent((int?)_socket.CloseStatus, _socket.CloseStatusDescription);
                yield break;
            }
            var bytes = result.Data;
            if (result.MessageType == WebSocketMessageType.Binary && bytes.Length > 0 && bytes[0] == 0)
            {
                var frame = JsonSerializer.Deserialize(bytes.AsSpan(1), PtyHttpJsonContext.Default.PtyCursorFrame)
                    ?? throw new JsonException("PTY cursor metadata is required.");
                if (frame.Cursor is < 0 or > 9007199254740991) throw new JsonException("Invalid PTY output cursor.");
                yield return new PtyCursorEvent(frame.Cursor);
                continue;
            }
            yield return new PtyTextEvent(utf8.GetString(bytes));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _abort.Dispose();
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
