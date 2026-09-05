namespace Transport;

using System.Net.WebSockets;

public sealed record SocketMessage(byte[] Data, WebSocketMessageType MessageType);

/// <summary>Owns fragment storage, never the WebSocket. Results own their bytes.</summary>
public sealed class PipelineWebSocket(WebSocket socket) : IDisposable
{
    private readonly SequenceBuffer _message = new();
    public async ValueTask<SocketMessage> ReadAsync(CancellationToken cancellationToken)
    {
        _message.Clear();
        while (true)
        {
            var result = await socket.ReceiveAsync(_message.GetMemory(65536)[..65536], cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) { _message.Clear(); return new([], result.MessageType); }
            _message.Commit(result.Count);
            if (!result.EndOfMessage) continue;
            var bytes = _message.ToArray();
            _message.Clear();
            return new(bytes, result.MessageType);
        }
    }
    public void Dispose() => _message.Dispose();
}
