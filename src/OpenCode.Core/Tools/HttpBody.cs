namespace OpenCode.Core.Tools;
using Transport;

internal static class HttpBody
{
    public static async Task<byte[]> CollectAsync(HttpResponseMessage response, int maximumBytes, CancellationToken ct)
    {
        var declared = response.Content.Headers.ContentLength;
        if (declared is >= 0 and <= 9_007_199_254_740_991 && declared > maximumBytes)
            throw new ToolExecutionException($"Response too large (exceeds {maximumBytes} byte limit)");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await PipelineBytes.CollectAsync(stream, maximumBytes,
            () => new ToolExecutionException($"Response too large (exceeds {maximumBytes} byte limit)"), ct);
    }
}
