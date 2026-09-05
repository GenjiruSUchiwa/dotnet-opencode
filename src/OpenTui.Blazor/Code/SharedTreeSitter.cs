namespace OpenTui.Blazor.Code;

/// <summary>Shares the source's reusable parser client across mounted default code views.</summary>
internal static class SharedTreeSitter
{
    private static readonly Lock Gate = new();
    private static TreeSitterHighlighter? _current;
    private static int _references;

    internal static Lease Retain()
    {
        lock (Gate)
        {
            _current ??= new();
            _references++;
            return new(_current);
        }
    }

    internal sealed class Lease(TreeSitterHighlighter highlighter) : ICodeHighlighter, IAsyncDisposable
    {
        private bool _disposed;
        public Task<CodeHighlightResult> HighlightAsync(CodeHighlightRequest request, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return highlighter.HighlightAsync(request, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (--_references != 0) return;
                _current = null;
            }
            // A future mount can obtain a fresh client while the retired client drains.
            await highlighter.DisposeAsync();
        }
    }
}
