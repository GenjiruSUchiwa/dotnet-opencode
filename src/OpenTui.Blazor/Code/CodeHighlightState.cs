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
    private readonly AsyncLocal<RunScope?> _execution = new();
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
            _options.BaseHighlight == options.BaseHighlight && (_options.SyntaxRules ?? []).SequenceEqual(options.SyntaxRules ?? []) &&
            Equals(_options.OnHighlight, options.OnHighlight) && Equals(_options.OnChunks, options.OnChunks) &&
            (_options.InitialStyledText is null) == (options.InitialStyledText is null) &&
            (_options.InitialStyledText ?? []).SequenceEqual(options.InitialStyledText ?? [])) return;
        var contentChanged = _options.Content != options.Content;
        // CodeRenderable resets its initial-content policy when streaming changes.
        if (_options.Streaming != options.Streaming) _hadContent = false;
        if (_options.Filetype != options.Filetype || !ReferenceEquals(_options.Highlighter, options.Highlighter)) HasParser = false;
        // Keep the pending request's rules stable if the caller reuses a mutable list.
        _options = options with
        {
            SyntaxRules = options.SyntaxRules is { } rules ? Array.AsReadOnly(rules.ToArray()) : null,
            InitialStyledText = options.InitialStyledText is { } initial ? Array.AsReadOnly(initial.ToArray()) : null,
        };
        var revision = ++_revision;
        _parse?.Cancel();
        // Cancellation registrations may synchronously update or dispose this state.
        if (_disposed || revision != _revision) return;
        Diagnostic = null;
        IsPartial = false;
        if (string.IsNullOrEmpty(options.Filetype) || options.Content.Length == 0 ||
            options.Highlighter is null && options.OnHighlight is null && options.OnChunks is null && string.IsNullOrEmpty(options.BaseHighlight))
        {
            HasParser = false;
            Diagnostic = options.Highlighter is null && !string.IsNullOrEmpty(options.Filetype) ? "No syntax parser is configured." : null;
            Document = InitialDocument(_options);
            Visible = true;
            _rerun = false;
            _highlighting = false;
            Changed?.Invoke();
            return;
        }
        if (!options.Streaming || !_hadContent)
        {
            Visible = options.DrawUnstyledText;
            if (Visible) Document = InitialDocument(_options);
        }
        else
        {
            Visible = true;
            // CodeRenderable's content setter publishes new unstyled text when
            // requested, even after the initial streaming highlight completed.
            if (contentChanged && options.DrawUnstyledText) Document = InitialDocument(_options);
        }
        _rerun = true;
        Changed?.Invoke();
        if (_disposed || revision != _revision || _active) return;
        _active = true;
        _loop = Run();
    }

    private async Task Run()
    {
        await Task.Yield();
        var scope = new RunScope();
        _execution.Value = scope;
        try
        {
            while (_rerun && !_disposed)
            {
                _rerun = false;
                var options = _options;
                var revision = _revision;
                if (string.IsNullOrEmpty(options.Filetype) || options.Content.Length == 0) continue;
                _highlighting = true;
                if (options.Streaming) _hadContent = true;
                using var parse = new CancellationTokenSource();
                _parse = parse;
                try
                {
                    var result = options.Highlighter is { } highlighter
                        ? await highlighter.HighlightAsync(new(options.Content, options.Filetype, revision), parse.Token)
                        : new CodeHighlightResult(false, [], "No syntax parser is configured.");
                    if (_disposed || revision != _revision) continue;
                    var captures = result.Captures.ToList();
                    var rules = options.SyntaxRules ?? [];
                    if (options.OnHighlight is { } onHighlight)
                    {
                        var replacement = await onHighlight(captures, new(options.Content, options.Filetype, rules), parse.Token);
                        if (_disposed || revision != _revision) continue;
                        if (replacement is not null) captures = replacement.ToList();
                    }
                    var snapshot = Array.AsReadOnly(captures.ToArray());
                    var document = snapshot.Count > 0 || options.OnChunks is not null || !string.IsNullOrEmpty(options.BaseHighlight)
                        ? CodeProjection.Create(options, snapshot) : CodeDocument.Plain(options.Content);
                    if (options.OnChunks is { } onChunks)
                    {
                        var chunks = CodeChunks.FromDocument(document);
                        var replacement = await onChunks(chunks,
                            new(options.Content, options.Filetype, rules, snapshot), parse.Token);
                        if (_disposed || revision != _revision) continue;
                        document = CodeChunks.ToDocument((replacement ?? chunks).ToArray());
                    }
                    HasParser = result.HasParser;
                    IsPartial = result.HasParser && result.IsPartial;
                    Diagnostic = result.Warning;
                    Document = document;
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
        finally { scope.Active = false; _execution.Value = null; _active = false; _highlighting = false; }
    }

    private static CodeDocument InitialDocument(CodeOptions options) =>
        options.Content.Length > 0 && options.DrawUnstyledText && options.InitialStyledText is { } chunks
            ? CodeChunks.ToDocument(chunks) : CodeDocument.Plain(options.Content);

    private sealed class RunScope { internal bool Active { get; set; } = true; }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
        // Invalidate and synchronously cancel on the dispatcher before joining the
        // parser loop. Do not move cancellation callbacks to another continuation.
#pragma warning disable MA0042
            _revision++; _rerun = false; _highlighting = false; _parse?.Cancel();
#pragma warning restore MA0042
        }
        // A transform may await disposal of its own component. Joining this loop
        // from that callback would join itself. It is already invalidated/cancelled;
        // the loop retires after the callback returns. External disposal still joins.
        if (_execution.Value is { Active: true }) return;
        await _loop;
    }
}
