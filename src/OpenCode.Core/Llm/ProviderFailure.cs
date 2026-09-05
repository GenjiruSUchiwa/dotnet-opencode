namespace OpenCode.Core.Llm;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Positive failure evidence from packages/ai/src/provider-error.ts.</summary>
internal static class ProviderFailure
{
    private static readonly Regex ContextOverflow = Pattern(string.Join('|', new[]
    {
        @"prompt is too long", @"input is too long for requested model", @"exceeds the context window",
        @"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))",
        @"input token count.*exceeds the maximum", @"tokens in request more than max tokens allowed",
        @"maximum prompt length is \d+", @"reduce the length of the messages", @"maximum context length is \d+ tokens",
        @"exceeds (?:the )?maximum allowed input length of [\d,]+ tokens?",
        @"input \(\d+ tokens\) is longer than the model'?s context length \(\d+ tokens\)",
        @"exceeds the limit of \d+", @"exceeds the available context size", @"greater than the context length",
        @"context window exceeds limit", @"exceeded model token limit", @"context[_ ]length[_ ]exceeded",
        @"context length is only \d+ tokens", @"input length.*exceeds.*context length",
        @"prompt too long; exceeded (?:max )?context length", @"too large for model with \d+ maximum context length",
        @"prompt has [\d,]+ tokens?, but the configured context size is [\d,]+ tokens?",
        @"model_context_window_exceeded", @"range of input length should be", @"too many tokens",
        @"token limit exceeded", @"request_too_large", @"^4(?:00|13)\s*(status code)?\s*\(no body\)"
    }));
    private static readonly Regex OverflowExclusions = Pattern(@"^(throttling error|service unavailable):|rate limit|too many requests");
    private static readonly Regex PayloadTooLarge = Pattern(@"request entity too large|payload too large|request too large");
    private static readonly Regex ContentPolicy = Pattern(@"content[-_\s]?policy|content_filter|safety");
    private static readonly Regex Quota = Pattern(@"insufficient[-_\s]?quota|quota[-_\s]?exceeded");
    private static readonly Regex RateLimit = Pattern(@"rate increased too quickly|rate[-_\s]?limit|too[_\s]?many[_\s]?requests");
    private static readonly Regex ServerError = Pattern(@"\b(?:try again|(?:please |you can )?retry (?:the |this |your )?request|try (?:the |this |your )?request again|(?:currently |temporarily )?at capacity|overloaded|temporarily unavailable|service[-_\s]?unavailable|(?:server|internal)[-_\s]?error|server (?:is )?busy|provider returned (?:an )?error|resource[-_\s]?exhausted|upstream (?:connect|connection|request)|request buffer limit while retrying upstream)\b");

    internal static LlmFailure Classify(string message, int? status = null, string? body = null,
        JsonElement data = default, LlmHttpContext? http = null)
    {
        var codes = Codes(data).Concat(Codes(Parse(body))).Concat(Codes(Parse(message)))
            .Select(code => code.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var text = string.Join('\n', new[] { message, body ?? "" }.Where(value => value.Length > 0));
        return Reason(message, status, codes, text) with { Body = body, Http = http };
    }

    private static LlmFailure Reason(string message, int? status, HashSet<string> codes, string text)
    {
        if ((status is null or >= 400 and < 500) &&
            (codes.Overlaps(["context_length_exceeded", "model_context_window_exceeded", "request_too_large"]) ||
             !OverflowExclusions.IsMatch(text) && ContextOverflow.IsMatch(text)))
            return new LlmFailure.InvalidRequest(message) { Classification = LlmFailureClassification.ContextOverflow };
        if (status == 413 || PayloadTooLarge.IsMatch(text))
            return new LlmFailure.InvalidRequest(message) { Classification = LlmFailureClassification.PayloadTooLarge };
        if (ContentPolicy.IsMatch(text)) return new LlmFailure.ContentPolicy(message);
        if (codes.Overlaps(["insufficient_quota", "usage_not_included", "billing_error"]) || status == 429 && Quota.IsMatch(text))
            return new LlmFailure.QuotaExceeded(message);
        if (status is 401 or 403 || codes.Overlaps(["authentication_error", "permission_error"]))
            return new LlmFailure.Authentication(message);
        if (status == 429 || codes.Any(code => code.Contains("rate_limit", StringComparison.Ordinal) || code is "too_many_requests" or "throttlingexception") || RateLimit.IsMatch(text))
            return new LlmFailure.RateLimit(message);
        var invalid = codes.Overlaps(["invalid_prompt", "invalid_request_error", "validationexception"]);
        if (status is 408 or 409 or >= 500 || (status is null or < 400) && !invalid && ServerError.IsMatch(text) ||
            codes.Overlaps(["api_error", "internal_error", "internalserverexception", "modelstreamerrorexception",
                "overloaded_error", "server_error", "server_is_overloaded", "slow_down", "serviceunavailableexception"]) ||
            codes.Any(code => code.Contains("exhausted", StringComparison.Ordinal) || code.Contains("unavailable", StringComparison.Ordinal)))
            return new LlmFailure.ProviderInternal(message);
        if (invalid || status is >= 400 and < 500) return new LlmFailure.InvalidRequest(message);
        return new LlmFailure.Provider(message);
    }

    private static IEnumerable<string> Codes(JsonElement value)
    {
        var error = Get(value, "error");
        var responseError = Get(Get(value, "response"), "error");
        var exception = Get(value, "exception");
        return new[] { Get(value, "code"), Get(error, "code"), Get(error, "type"), Get(error, "status"),
                Get(responseError, "code"), Get(exception, "type") }
            .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!);
    }

    private static JsonElement Get(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;

    private static JsonElement Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return default;
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
}
