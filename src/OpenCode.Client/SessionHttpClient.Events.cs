namespace OpenCode.Client;
using Transport;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    // Matches the generated Promise transport's 16 MiB string-buffer guard.
    private const int MaxSseEventCharacters = 16 * 1024 * 1024;

    /// <summary>One volatile SSE connection. No replay, automatic reconnection, prompt submission, or background worker.</summary>
    public async IAsyncEnumerable<ServerEventEnvelope> SubscribeEventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "/api/event", "text/event-stream");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
        await RequireSuccessAsync(response, "event.subscribe", cancellation.Token).ConfigureAwait(false);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            throw new SessionProtocolException(SessionProtocolFailure.UnsupportedContentType, "event.subscribe", "Expected text/event-stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
        var frameCharacters = 0;
        await using var lines = PipelineText.LinesAsync(stream, new UTF8Encoding(false, true), detectBom: false,
            stripInitialBom: true, maximumCharacters: MaxSseEventCharacters,
            tooLarge: EventTooLarge, remainingCharacters: () => MaxSseEventCharacters - frameCharacters,
            cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var data = new List<string>();
        while (await ReadEventLineAsync(lines).ConfigureAwait(false))
        {
            var line = lines.Current;
            frameCharacters += line.Text.Length + (line.Terminated ? 1 : 0);
            if (frameCharacters > MaxSseEventCharacters) throw EventTooLarge();
            if (line.Text.Length == 0 && line.Terminated)
            {
                var text = string.Join('\n', data);
                if (text.Length != 0)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    yield return ParseEvent(text);
                }
                data.Clear();
                frameCharacters = 0;
                continue;
            }
            AppendEventData(line.Text, data);
        }
        // The Promise client also accepts a final data block without a blank-line terminator.
        var final = string.Join('\n', data);
        if (final.Length != 0)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            yield return ParseEvent(final);
        }
    }

    private static Exception EventTooLarge() => new SessionProtocolException(SessionProtocolFailure.EventTooLarge,
        "event.subscribe", "SSE event exceeded the 16 MiB character-buffer limit.");

    private static async ValueTask<bool> ReadEventLineAsync(IAsyncEnumerator<TextRecord> lines)
    {
        try { return await lines.MoveNextAsync().ConfigureAwait(false); }
        catch (DecoderFallbackException error) { throw Malformed("event.subscribe", "SSE contains invalid UTF-8.", error); }
    }

    private static void AppendEventData(string text, List<string> data)
    {
        if (text != "data" && !text.StartsWith("data:", StringComparison.Ordinal)) return;
        var value = text.Length == 4 ? ReadOnlySpan<char>.Empty : text.AsSpan(5);
        if (!value.IsEmpty && value[0] == ' ') value = value[1..];
        data.Add(value.ToString());
    }

    private static ServerEventEnvelope ParseEvent(string data)
    {
        try
        {
            var raw = JsonSerializer.Deserialize(data, SessionHttpJsonContext.Default.JsonElement);
            if (raw.ValueKind != JsonValueKind.Object) throw new JsonException("Event must be an object.");
            if (raw.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "server.connected" && !raw.TryGetProperty("created", out _))
            {
                var connected = raw.Deserialize(SessionHttpJsonContext.Default.ConnectedEventFrame)
                    ?? throw new JsonException("Connected frame must not be null.");
                return new ServerEventEnvelope(connected.Id, connected.Type, null, connected.Location, null, raw);
            }
            var envelope = raw.Deserialize(OpenCodeJsonContext.Default.OpenCodeEvent)
                ?? throw new JsonException("Event must not be null.");
            return new ServerEventEnvelope(envelope.Id, envelope.Type, envelope.Created, envelope.Location, envelope.Durable, raw);
        }
        catch (JsonException error) { throw Malformed("event.subscribe", "Event does not satisfy the canonical envelope contract.", error); }
        catch (ArgumentException error) { throw Malformed("event.subscribe", "Event contains an invalid identifier or location.", error); }
        catch (NotSupportedException error)
        {
            throw new SessionProtocolException(SessionProtocolFailure.UnsupportedSchema, "event.subscribe", "Event envelope requires unavailable schema support.", error);
        }
    }
}
