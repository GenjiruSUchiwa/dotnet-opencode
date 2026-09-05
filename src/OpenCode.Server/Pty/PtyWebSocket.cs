namespace OpenCode.Server.Pty;
using Transport;

using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using OpenCode.Core.Pty;
using OpenCode.Schema;
using OpenCode.Server.Endpoints;

public static class PtyWebSocket
{
    private sealed record Frame(ReadOnlyMemory<byte> Data, WebSocketMessageType Type, bool Close = false);

    public static long? Cursor(HttpRequest request)
    {
        var value = RequestLocation.QueryValue(request, "cursor");
        if (value is null) return null;
        return ParseNumber(value, -1, 9007199254740991);
    }

    internal static long? ParseNumber(string value, long minimum, long maximum)
    {
        const string space = @"\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        value = Regex.Replace(value, @"\A[" + space + @"]+|[" + space + @"]+\z", "");
        var radix = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : value.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2 : value.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        double number;
        if (radix != 0)
        {
            try { number = Convert.ToUInt64(value[2..], radix); }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return null; }
        }
        else if (!double.TryParse(value.Length == 0 ? "0" : value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
        return double.IsFinite(number) && number == Math.Truncate(number) && number >= minimum && number <= maximum ? (long)number : null;
    }

    public static async Task RunAsync(HttpContext context, PtyService runtime, PtyId id, CancellationToken stopping)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var clock = context.RequestServices.GetRequiredService<TimeProvider>();
        using var cancellation = clock.CreateLinkedCancellationTokenSource(context.RequestAborted, stopping);
        var outbox = Channel.CreateUnbounded<Frame>(new() { SingleReader = true, AllowSynchronousContinuations = false });
        PtyAttachment attachment;
        try
        {
            attachment = runtime.Attach(id,
                output => outbox.Writer.TryWrite(new(Encoding.UTF8.GetBytes(output.Text), WebSocketMessageType.Text)),
                _ => outbox.Writer.TryWrite(new(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Close, true)), Cursor(context.Request));
        }
        catch (Exception error) when (error is PtyNotFoundException or PtyExitedException or ObjectDisposedException)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
            try { await socket.CloseOutputAsync((WebSocketCloseStatus)4404, error is PtyExitedException ? "session exited" : "session not found", timeout.Token); }
            catch (Exception closeError) when (closeError is OperationCanceledException or WebSocketException) { }
            return;
        }

        using (attachment)
        {
            for (var offset = 0; offset < attachment.Replay.Length; offset += 64 * 1024)
                outbox.Writer.TryWrite(new(Encoding.UTF8.GetBytes(attachment.Replay.Substring(offset, Math.Min(64 * 1024, attachment.Replay.Length - offset))), WebSocketMessageType.Text));
            outbox.Writer.TryWrite(new(Encoding.UTF8.GetBytes("\0{\"cursor\":" + attachment.Cursor.ToString(CultureInfo.InvariantCulture) + "}"), WebSocketMessageType.Binary));
            attachment.Activate();
            var drain = DrainAsync();
            var receive = ReceiveAsync();
            try
            {
                var finished = await Task.WhenAny(drain, receive);
                if (finished == receive)
                {
                    await receive;
                    // A peer close is sent through the same writer, after all accepted output.
                    cancellation.CancelAfter(TimeSpan.FromSeconds(1));
                    await drain;
                }
                else await drain;
            }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException) { }
            finally
            {
                cancellation.Cancel();
                outbox.Writer.TryComplete();
                // Observe both tasks before disposing the socket or attachment.
                await Task.WhenAll(drain, receive).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        async Task DrainAsync()
        {
            await foreach (var frame in outbox.Reader.ReadAllAsync(cancellation.Token))
            {
                if (frame.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellation.Token);
                    return;
                }
                await socket.SendAsync(frame.Data, frame.Type, true, cancellation.Token);
            }
        }

        async Task ReceiveAsync()
        {
            var utf8 = new UTF8Encoding(false, true);
            using var messages = new PipelineWebSocket(socket);
            while (!cancellation.IsCancellationRequested)
            {
                var result = await messages.ReadAsync(cancellation.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    outbox.Writer.TryWrite(new(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Close, true));
                    return;
                }
                string text;
                try { text = utf8.GetString(result.Data); }
                catch (DecoderFallbackException) { continue; }
                // TextDecoder removes a leading UTF-8 BOM from each binary input message.
                if (result.MessageType == WebSocketMessageType.Binary && text.StartsWith('\uFEFF')) text = text[1..];
                // A synchronous ConPTY pipe write already in progress may outlive cancellation.
                // The Location still owns that write; do not strand the WebSocket teardown on it.
                await attachment.WriteAsync(Encoding.UTF8.GetBytes(text), cancellation.Token).AsTask().WaitAsync(cancellation.Token);
            }
        }
    }
}
