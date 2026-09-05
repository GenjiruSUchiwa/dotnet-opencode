namespace OpenTui.Blazor.Code;

/// <summary>Single-flight parser scheduling with source snapshots and stale-result rejection.</summary>
public sealed class CodeHighlightState : IAsyncDisposable
{
    private CodeOptions _options = new("");
    private CancellationTokenSource? _parse;
    private Task _loop = Task.CompletedTask;
    private bool _active;
    private bool _rerun;
    private bool _disposed;
    private bool _hadContent;
    private long _revision;
    public CodeDocument Document { get; private set; } = CodeDocument.Plain("");
    public bool Visible { get; private set; } = true;
    public bool Highlighting => _active || _rerun;
    public bool HasParser { get; private set; }
    public string? Diagnostic { get; private set; }
    public Task HighlightingDone => _loop;
    public event Action? Changed;

    public void Update(CodeOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_options.Content == options.Content && _options.Filetype == options.Filetype && ReferenceEquals(_options.Highlighter, options.Highlighter) &&
            _options.Conceal == options.Conceal && _options.DrawUnstyledText == options.DrawUnstyledText && _options.Streaming == options.Streaming &&
            _options.BaseHighlight == options.BaseHighlight && (_options.SyntaxRules ?? []).SequenceEqual(options.SyntaxRules ?? [])) return;
        // CodeRenderable resets its initial-content policy when streaming changes.
        if (_options.Streaming != options.Streaming) _hadContent = false;
        if (_options.Filetype != options.Filetype || !ReferenceEquals(_options.Highlighter, options.Highlighter)) HasParser = false;
        // Keep the pending request's rules stable if the caller reuses a mutable list.
        _options = options with { SyntaxRules = options.SyntaxRules?.ToArray() };
        _revision++;
        _parse?.Cancel();
        Diagnostic = null;
        if (options.Highlighter is null || string.IsNullOrEmpty(options.Filetype) || options.Content.Length == 0)
        {
            HasParser = false;
            Diagnostic = options.Highlighter is null && !string.IsNullOrEmpty(options.Filetype) ? "No syntax parser is configured." : null;
            Document = CodeDocument.Plain(options.Content);
            Visible = true;
            _rerun = false;
            Changed?.Invoke();
            return;
        }
        if (!options.Streaming || !_hadContent)
        {
            Visible = options.DrawUnstyledText;
            if (Visible) Document = CodeDocument.Plain(options.Content);
        }
        _hadContent = true;
        _rerun = true;
        Changed?.Invoke();
        if (_active) return;
        _active = true;
        _loop = Run();
    }

    private async Task Run()
    {
        await Task.Yield();
        try
        {
            while (_rerun && !_disposed)
            {
                _rerun = false;
                var options = _options;
                var revision = _revision;
                if (options.Highlighter is null || string.IsNullOrEmpty(options.Filetype)) continue;
                using var parse = new CancellationTokenSource();
                _parse = parse;
                try
                {
                    var result = await options.Highlighter.HighlightAsync(new(options.Content, options.Filetype, revision), parse.Token);
                    if (_disposed || revision != _revision) continue;
                    HasParser = result.HasParser;
                    Diagnostic = result.Warning;
                    Document = result.HasParser ? CodeProjection.Create(options, result.Captures) : CodeDocument.Plain(options.Content);
                    Visible = true;
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (parse.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (_disposed || revision != _revision) continue;
                    HasParser = false; Diagnostic = exception.Message;
                    Document = CodeDocument.Plain(options.Content); Visible = true;
                    Changed?.Invoke();
                }
                finally { if (ReferenceEquals(_parse, parse)) _parse = null; }
            }
        }
        finally { _active = false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Invalidate and synchronously cancel on the dispatcher before joining the
        // parser loop. Do not move cancellation callbacks to another continuation.
#pragma warning disable MA0042
        _revision++; _rerun = false; _parse?.Cancel();
#pragma warning restore MA0042
        await _loop;
    }
}
