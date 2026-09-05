namespace OpenCode.Core.Session;

using System.Globalization;
using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Schema;

internal sealed record SessionRetryDecision(int Attempt, long Delay);
internal abstract record SessionAttemptOutcome
{
    internal sealed record Completed(bool NeedsContinuation) : SessionAttemptOutcome;
    internal sealed record Retry(SessionStructuredError Error, SessionRetryDecision Decision) : SessionAttemptOutcome;
    internal sealed record Continue(SessionStructuredError Error, SessionRetryDecision Decision) : SessionAttemptOutcome;
    internal sealed record Compacted : SessionAttemptOutcome;
}

/// <summary>runner/retry.ts defaults, without unsupported plugin retry hooks.</summary>
internal sealed class SessionRetry(TimeProvider clock)
{
    private int _retries;

    internal SessionRetryDecision? Decide(LlmException error, bool interruptedStream = false)
    {
        if (_retries == 4 || error.Reason is LlmFailure.InvalidRequest { Classification: LlmFailureClassification.ContextOverflow }) return null;
        var directive = Header(error, "x-should-retry");
        var retry = directive switch
        {
            "true" => true,
            "false" => false,
            // The source proposes a continuation for post-output interrupted streams,
            // independently of generic pre-output delivery eligibility. Headers still win.
            _ => interruptedStream || (error.Reason switch
            {
                LlmFailure.RateLimit or LlmFailure.ProviderInternal or LlmFailure.Provider => true,
                LlmFailure.Transport { Delivery: null or LlmTransportDelivery.NotSent } => true,
                LlmFailure.InvalidProviderOutput output => output.IncompleteStream,
                _ => false
            })
        };
        if (!retry) return null;
        var delay = 2000 * Math.Pow(2, _retries) * (0.8 + Random.Shared.NextDouble() * 0.4);
        if (error.Reason is LlmFailure.RateLimit or LlmFailure.ProviderInternal)
        {
            var millis = Number(Header(error, "retry-after-ms"));
            if (millis is null && Header(error, "retry-after") is { Length: > 0 } after)
            {
                millis = Number(after) is { } seconds ? seconds * 1000 :
                    DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date)
                        ? date.ToUnixTimeMilliseconds() - clock.GetUtcNow().ToUnixTimeMilliseconds() : null;
            }
            if (millis is { } minimum && double.IsFinite(minimum)) delay = Math.Max(delay, Math.Clamp(minimum, 0, 15 * 60 * 1000));
        }
        _retries++;
        return new SessionRetryDecision(_retries + 1, checked((long)Math.Ceiling(delay)));
    }

    internal static async Task WaitAsync(SessionStore store, SessionId sessionId, MessageId assistantId,
        SessionAttemptOutcome.Retry retry, CancellationToken ct)
    {
        var at = store.Clock.GetUtcNow().ToUnixTimeMilliseconds() + retry.Decision.Delay;
        using (var publication = new CancellationTokenSource(TimeSpan.FromSeconds(15), store.Clock))
            await store.AppendAssistantEventAsync("session.retry.scheduled", JsonSerializer.SerializeToElement(
                new SessionRetryScheduledEventData(sessionId, assistantId, retry.Decision.Attempt, at, retry.Error),
                OpenCodeJsonContext.Default.SessionRetryScheduledEventData), publication.Token);
        var remaining = Math.Max(0, at - store.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        await Task.Delay(TimeSpan.FromMilliseconds(remaining), store.Clock, ct);
    }

    private static string? Header(LlmException error, string name) => error.Reason.Http?.Headers
        .FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static double? Number(string? value)
    {
        if (value is null) return null;
        if (value.Trim().Length == 0) return 0;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
    }
}
