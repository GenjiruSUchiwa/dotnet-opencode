namespace OpenTui.Blazor.Clipboard;

using OpenTui.Native;

/// <summary>Host clipboard only. No OSC52 read fallback, source fetching, file admission, or startup clipboard read.</summary>
public sealed class NativeHostClipboard(NativeClipboardOptions? options = null, TimeProvider? clock = null) : IRichClipboard, IAsyncDisposable
{
    private readonly NativeClipboardService _native = new(options, clock);

    public async Task<ClipboardReadResult> ReadAsync(ClipboardReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _native.ReadAsync(request.PreferredTypes, Selection(request.Selection), cancellationToken);
        return result.Status switch
        {
            NativeClipboardOperationStatus.Read when result.MimeType is { Length: > 0 } mime =>
                new(ClipboardReadStatus.Read, new(mime, result.Bytes)),
            NativeClipboardOperationStatus.Empty => new(ClipboardReadStatus.Empty),
            NativeClipboardOperationStatus.Unsupported => new(ClipboardReadStatus.Unsupported),
            NativeClipboardOperationStatus.Cancelled => new(ClipboardReadStatus.Cancelled),
            NativeClipboardOperationStatus.TimedOut => new(ClipboardReadStatus.TimedOut),
            NativeClipboardOperationStatus.LimitExceeded => new(ClipboardReadStatus.LimitExceeded),
            _ => new(ClipboardReadStatus.Failed, Error: Error(result))
        };
    }

    public async Task<ClipboardMutationResult> WriteHostTextAsync(string text, ClipboardSelection selection = ClipboardSelection.Clipboard,
        CancellationToken cancellationToken = default) => Mutation(await _native.WriteTextAsync(text, Selection(selection), cancellationToken));
    public async Task<ClipboardMutationResult> ClearAsync(ClipboardSelection selection = ClipboardSelection.Clipboard,
        CancellationToken cancellationToken = default) => Mutation(await _native.ClearAsync(Selection(selection), cancellationToken));

    // Compatibility contract: writes that do not succeed must not look like a
    // successful Task to existing copy-message/code consumers.
    public async Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await WriteHostTextAsync(text, cancellationToken: cancellationToken);
        if (result.Status == ClipboardMutationStatus.Written) return;
        if (result.Status == ClipboardMutationStatus.Cancelled) throw new OperationCanceledException(cancellationToken);
        throw new InvalidOperationException(result.Error?.Message ?? $"Host clipboard write returned {result.Status}.");
    }

    public ValueTask DisposeAsync() => _native.DisposeAsync();
    private static NativeClipboardSelection Selection(ClipboardSelection selection) => selection switch
    {
        ClipboardSelection.Clipboard => NativeClipboardSelection.Clipboard,
        ClipboardSelection.Primary => NativeClipboardSelection.Primary,
        _ => throw new ArgumentOutOfRangeException(nameof(selection))
    };
    private static ClipboardError Error(NativeClipboardResult result) => new(result.Diagnostic ?? "Native clipboard operation failed.", result.ErrorCode);
    private static ClipboardMutationResult Mutation(NativeClipboardResult result) => result.Status switch
    {
        NativeClipboardOperationStatus.Written => new(ClipboardMutationStatus.Written),
        NativeClipboardOperationStatus.Cleared => new(ClipboardMutationStatus.Cleared),
        NativeClipboardOperationStatus.Unsupported => new(ClipboardMutationStatus.Unsupported),
        NativeClipboardOperationStatus.Cancelled => new(ClipboardMutationStatus.Cancelled),
        NativeClipboardOperationStatus.TimedOut => new(ClipboardMutationStatus.TimedOut),
        _ => new(ClipboardMutationStatus.Failed, Error(result))
    };
}
