namespace OpenTui.Blazor.Code;

/// <summary>Single-flight parser scheduling with source snapshots and stale-result rejection.</summary>
public sealed class CodeHighlightState : IAsyncDisposable
{
    private CodeOptions _options = new("");
    private CancellationTokenSource? _parse;
    private Task _loop = Task.CompletedTask;
    private bool _active;
    private bool _highlighting;
    private bool _rerun;
    private bool _disposed;
    private bool _hadContent;
    private long _revision;
    public CodeDocument Document { get; private set; } = CodeDocument.Plain("");
    public bool Visible { get; private set; } = true;
    public bool Highlighting => _highlighting || _rerun;
    public bool HasParser { get; private set; }
    public bool IsPartial { get; private set; }
    public string? Diagnostic { get; private set; }
    public Task HighlightingDone => _loop;
    public event Action? Changed;

    public void Update(CodeOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_options.Content == options.Content && _options.Filetype == options.Filetype && ReferenceEquals(_options.Highlighter, options.Highlighter) &&
            _options.Conceal == options.Conceal && _options.DrawUnstyledText == options.DrawUnstyledText && _options.Streaming == options.Streaming &&
            _options.BaseHighlight == options.BaseHighlight && (_options.SyntaxRules ?? []).SequenceEqual(options.SyntaxRules ?? [])) return;
        var contentChanged = _options.Content != options.Content;
        // CodeRenderable resets its initial-content policy when streaming changes.
        if (_options.Streaming != options.Streaming) _hadContent = false;
        if (_options.Filetype != options.Filetype || !ReferenceEquals(_options.Highlighter, options.Highlighter)) HasParser = false;
        // Keep the pending request's rules stable if the caller reuses a mutable list.
        _options = options with { SyntaxRules = options.SyntaxRules?.ToArray() };
        _revision++;
        _parse?.Cancel();
        Diagnostic = null;
        IsPartial = false;
        if (options.Highlighter is null || string.IsNullOrEmpty(options.Filetype) || options.Content.Length == 0)
        {
            HasParser = false;
            Diagnostic = options.Highlighter is null && !string.IsNullOrEmpty(options.Filetype) ? "No syntax parser is configured." : null;
            Document = CodeDocument.Plain(options.Content);
            Visible = true;
            _rerun = false;
            _highlighting = false;
            Changed?.Invoke();
            return;
        }
        if (!options.Streaming || !_hadContent)
        {
            Visible = options.DrawUnstyledText;
            if (Visible) Document = CodeDocument.Plain(options.Content);
        }
        else
        {
            Visible = true;
            // CodeRenderable's content setter publishes new unstyled text when
            // requested, even after the initial streaming highlight completed.
            if (contentChanged && options.DrawUnstyledText) Document = CodeDocument.Plain(options.Content);
        }
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
                _highlighting = true;
                if (options.Streaming) _hadContent = true;
                using var parse = new CancellationTokenSource();
                _parse = parse;
                try
                {
                    var result = await options.Highlighter.HighlightAsync(new(options.Content, options.Filetype, revision), parse.Token);
                    if (_disposed || revision != _revision) continue;
                    HasParser = result.HasParser;
                    IsPartial = result.HasParser && result.IsPartial;
                    Diagnostic = result.Warning;
                    Document = result.HasParser && (result.Captures.Count > 0 || options.BaseHighlight is not null)
                        ? CodeProjection.Create(options, result.Captures) : CodeDocument.Plain(options.Content);
                    Visible = true;
                    _highlighting = false;
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (parse.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (_disposed || revision != _revision) continue;
                    HasParser = false; IsPartial = false; Diagnostic = exception.Message;
                    Document = CodeDocument.Plain(options.Content); Visible = true;
                    _highlighting = false;
                    Changed?.Invoke();
                }
                finally { _highlighting = false; if (ReferenceEquals(_parse, parse)) _parse = null; }
            }
        }
        finally { _active = false; _highlighting = false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // Invalidate and synchronously cancel on the dispatcher before joining the
        // parser loop. Do not move cancellation callbacks to another continuation.
#pragma warning disable MA0042
        _revision++; _rerun = false; _highlighting = false; _parse?.Cancel();
#pragma warning restore MA0042
        await _loop;
    }
}
