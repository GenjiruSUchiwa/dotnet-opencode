namespace OpenCode.Core.Llm;

using OpenCode.Schema;

/// <summary>Keeps the rewrite's product identity visible after provider/host header overlays.</summary>
internal static class ProviderUserAgent
{
    internal static void Apply(HttpRequestMessage request)
    {
        var configured = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(' ', values) : null;
        var value = string.IsNullOrWhiteSpace(configured) ? OpenCodeChannel.UserAgent
            : configured.Equals(OpenCodeChannel.UserAgent, StringComparison.Ordinal)
                || configured.StartsWith(OpenCodeChannel.UserAgent + "/", StringComparison.Ordinal)
                || configured.StartsWith(OpenCodeChannel.UserAgent + " ", StringComparison.Ordinal)
                || configured.StartsWith(OpenCodeChannel.UserAgent + "\t", StringComparison.Ordinal)
                ? configured : OpenCodeChannel.UserAgent + " " + configured;
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", value);
    }
}
