namespace OpenCode.Server.Pty;
using Transport;

using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Endpoints;

public static class PersistentPtyWebSocket
{
    private sealed record Frame(byte[] Bytes, WebSocketMessageType Type, WebSocketCloseStatus? Close = null, string? Reason = null);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static async Task RunAsync(HttpContext context, PersistentPtyService runtime, PtyId id, long cursor, CancellationToken stopping)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var outbox = Channel.CreateUnbounded<Frame>(new() { SingleReader = true, AllowSynchronousContinuations = false });
        var attachmentId = RequestLocation.QueryValue(context.Request, "attachment_id") ?? Guid.NewGuid().ToString();
        var role = RequestLocation.QueryValue(context.Request, "role") == "observer" ? "observer" : "controller";
        var framed = RequestLocation.QueryValue(context.Request, "input_protocol") == "1";
        PersistentPtyAttachment attachment;
        try
        {
            attachment = await runtime.AttachAsync(id, cursor, attachmentId, role,
                RequestLocation.QueryValue(context.Request, "takeover") == "true", OnEvent,
                () => outbox.Writer.TryWrite(new([], WebSocketMessageType.Close, WebSocketCloseStatus.NormalClosure)), cancellation.Token);
        }
        catch (Exception error) when (error is IOException or PtyNotFoundException or System.ComponentModel.Win32Exception or JsonException
            or System.Net.Sockets.SocketException or InvalidOperationException or NotSupportedException or FormatException or KeyNotFoundException)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), context.RequestServices.GetRequiredService<TimeProvider>());
            try { await socket.CloseOutputAsync((WebSocketCloseStatus)4404, "terminal unavailable", deadline.Token); }
            catch (Exception closed) when (closed is WebSocketException or OperationCanceledException) { }
            return;
        }
        await using (attachment)
        {
            SendControl(new
            {
                type = "attached", attachmentID = attachmentId, inputProtocol = framed ? 1 : 0,
                info = attachment.Info, role = attachment.Role, generation = attachment.Generation,
                replay = new { requestedOffset = attachment.Replay.RequestedOffset, availableOffset = attachment.Replay.AvailableOffset,
                    endOffset = attachment.Replay.EndOffset, truncated = attachment.Replay.Truncated }
            });
            if (attachment.Replay.Data.Length > 0) outbox.Writer.TryWrite(new(attachment.Replay.Data, WebSocketMessageType.Binary));
            SendControl(new { type = "replay_complete", endOffset = attachment.Replay.EndOffset });
            attachment.Activate();
            var writing = DrainAsync();
            var reading = ReceiveAsync();
            try { await await Task.WhenAny(writing, reading); }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
            finally
            {
                await cancellation.CancelAsync();
                outbox.Writer.TryComplete();
                await Task.WhenAll(writing, reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            async Task ReceiveAsync()
            {
                using var messages = new PipelineWebSocket(socket);
                while (true)
                {
                    var packet = await messages.ReadAsync(cancellation.Token);
                    if (packet.MessageType == WebSocketMessageType.Close) return;
                    var data = packet.Data;
                    try
                    {
                        if (!framed) { await runtime.InputAsync(id, attachmentId, attachment.Info.Size, data, ct: cancellation.Token); continue; }
                        if (data.Length < 5 || data[0] is not (0 or 1)) continue;
                        var cols = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(1));
                        var rows = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(3));
                        if (cols == 0 || rows == 0) continue;
                        await runtime.InputAsync(id, attachmentId, new(cols, rows), data.AsMemory(5), data[0] == 0, cancellation.Token);
                    }
                    catch (Exception error) when (error is IOException or PtyNotFoundException)
                    {
                        // Source framed-input operations ignore rejected control/input requests.
                    }
                }
            }
        }

        async Task DrainAsync()
        {
            await foreach (var frame in outbox.Reader.ReadAllAsync(cancellation.Token))
            {
                if (frame.Close is { } close) { await socket.CloseOutputAsync(close, frame.Reason, cancellation.Token); return; }
                await socket.SendAsync(frame.Bytes.AsMemory(), frame.Type, true, cancellation.Token);
            }
        }
        void SendControl(object value) => outbox.Writer.TryWrite(new(JsonSerializer.SerializeToUtf8Bytes(value, Json), WebSocketMessageType.Text));
        void OnEvent(PersistentPtyStreamEvent change)
        {
            switch (change)
            {
                case PersistentPtyOutputEvent output: outbox.Writer.TryWrite(new(output.Data, WebSocketMessageType.Binary)); break;
                case PersistentPtyResizeEvent resized: SendControl(new { type = "resized", cols = resized.Cols, rows = resized.Rows,
                    generation = resized.Generation, checkpoint = Convert.ToBase64String(resized.Checkpoint) }); break;
                case PersistentPtyExitEvent exited: SendControl(new { type = "exited", exitCode = exited.ExitCode, finalOffset = exited.FinalOffset }); break;
                case PersistentPtyControllerEvent controller: SendControl(new { type = "controller_changed", attachmentID = controller.AttachmentId,
                    generation = controller.Generation }); break;
                case PersistentPtyTitleEvent title: SendControl(new { type = "title_changed", title = title.Title }); break;
                case PersistentPtyForegroundEvent foreground:
                    outbox.Writer.TryWrite(new(Encoding.UTF8.GetBytes("{\"type\":\"foreground_process_changed\",\"process\":" + JsonSerializer.Serialize(foreground.Process) + "}"), WebSocketMessageType.Text));
                    break;
            }
        }
    }
}
