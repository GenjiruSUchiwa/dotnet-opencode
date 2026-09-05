namespace OpenTui.Blazor.Code;

/// <summary>Lazy, serialized .NET host for the bundled upstream Tree-sitter WASM grammars.</summary>
public sealed class TreeSitterHighlighter : ICodeHighlighter, IAsyncDisposable
{
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, Parser> _parsers = new(StringComparer.Ordinal);
    private TreeSitterWasm? _host;
    private bool _disposed;
    private readonly TreeSitterGrammarRegistry _registry;
    private readonly TreeSitterGrammarCache? _cache;
    private TreeSitterGrammarRegistry.Snapshot _snapshot;

    public TreeSitterHighlighter(TimeProvider? clock = null) : this(new TreeSitterGrammarRegistry(), null, clock) { }

    /// <summary>The caller owns the registry and cache and disposes the cache after its highlighters.</summary>
    public TreeSitterHighlighter(TreeSitterGrammarRegistry registry, TreeSitterGrammarCache? cache, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _registry = registry;
        _cache = cache;
        _snapshot = registry.Capture();
    }

    public async Task<CodeHighlightResult> HighlightAsync(CodeHighlightRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var snapshot = _registry.Capture();
            if (_snapshot.Revision != snapshot.Revision)
            {
                // Grammar instances own table/data segments for their store lifetime.
                // Retire the store at a serialized request boundary on replacement.
                DisposeRuntime();
                _snapshot = snapshot;
            }
            var language = _snapshot.Resolve(request.Filetype);
            if (language is null) return new(false, [], $"No registered Tree-sitter grammar for '{request.Filetype}'.");
            // The UI dispatcher is never occupied by Wasmtime compilation, parsing, or queries.
            return await Task.Run(() => Highlight(request.Content, language, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            DisposeRuntime();
            return new(false, [], exception.Message);
        }
        finally { _gate.Release(); }
    }

    private async Task<CodeHighlightResult> Highlight(string content, string language, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        _host ??= TreeSitterWasm.Create(_clock);
        var parser = await GetParser(language, cancellation).ConfigureAwait(false);
        var tree = Parse(parser, content, cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var captures = parser.Highlights.Captures(tree, content, cancellation).Select(capture => Convert(capture, 0)).ToList();
            var ranges = new List<(int Start, int End, string Language)>();
            var warnings = new List<string>();
            if (parser.Injections is not null)
            {
                var groups = new Dictionary<string, List<TreeSitterQuery.Capture>>(StringComparer.Ordinal);
                foreach (var capture in parser.Injections.Captures(tree, content, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!capture.Name.Contains("injection", StringComparison.Ordinal)) continue;
                    var type = capture.Node.Type(_host, tree, parser.Language);
                    var registration = _snapshot.Registrations.GetValueOrDefault(language);
                    var target = registration is null
                        ? (type is "inline" or "pipe_table_cell" ? "markdown_inline" : null)
                        : registration.InjectionNodeTypes?.GetValueOrDefault(type);
                    if (string.IsNullOrEmpty(target) && type == "code_fence_content")
                    {
                        var parent = capture.Node.Parent(_host, tree);
                        var info = parent.Id == 0 ? default : parent.ChildOfType(_host, tree, parser.Language, "info_string");
                        var node = info.Id == 0 ? default : info.ChildOfType(_host, tree, parser.Language, "language");
                        if (node.Id != 0)
                        {
                            node.Write(_host);
                            target = content[node.Start.._host.Call("ts_node_end_index_wasm", tree)];
                            var mapped = registration?.InjectionInfoStrings?.GetValueOrDefault(target);
                            if (!string.IsNullOrEmpty(mapped)) target = mapped;
                            if (registration is null) target = target switch
                            {
                                "js" => "javascript", "jsx" => "javascriptreact",
                                "ts" => "typescript", "tsx" => "typescriptreact", "md" => "markdown", _ => target,
                            };
                        }
                    }
                    // This matches OpenTUI's node-type/info-string injection routing, not
                    // a generic Neovim directive interpreter or recursive injection engine.
                    if (string.IsNullOrEmpty(target)) continue;
                    if (!groups.TryGetValue(target, out var group)) groups.Add(target, group = []);
                    group.Add(capture);
                }
                foreach (var group in groups)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var target = _snapshot.Resolve(group.Key);
                    if (target is null)
                    {
                        warnings.Add($"No registered parser for injection language '{group.Key}'.");
                        continue;
                    }
                    Parser injected;
                    try { injected = await GetParser(target, cancellation).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        warnings.Add($"Injection parser '{group.Key}' could not load: {exception.Message}");
                        continue;
                    }
                    foreach (var capture in group.Value)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var injectedTree = 0;
                        try
                        {
                            // The worker registers the container before parsing,
                            // including when that particular injection later fails.
                            ranges.Add((capture.Node.Start, capture.End, group.Key));
                            var text = content[capture.Node.Start..capture.End];
                            injectedTree = Parse(injected, text, cancellation);
                            cancellation.ThrowIfCancellationRequested();
                            captures.AddRange(injected.Highlights.Captures(injectedTree, text, cancellation)
                                .Select(item => Convert(item, capture.Node.Start, injected: true)));
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                        catch (Exception exception) { warnings.Add($"Injection '{group.Key}' could not be highlighted: {exception.Message}"); }
                        finally { if (injectedTree != 0) _host.Call("ts_tree_delete", injectedTree); }
                    }
                }
            }
            // Preserve the source worker's ordered first containing/contained range match,
            // including base captures wholly within an injection container.
            var result = captures.Select(capture =>
            {
                foreach (var range in ranges)
                {
                    if (capture.Start >= range.Start && capture.End <= range.End)
                        return capture with { Metadata = capture.Metadata! with { IsInjection = true, InjectionLanguage = range.Language } };
                    if (capture.Start <= range.Start && capture.End >= range.End)
                        return capture with { Metadata = capture.Metadata! with { ContainsInjection = true } };
                }
                return capture;
            }).OrderBy(capture => capture.Start).ToArray();
            cancellation.ThrowIfCancellationRequested();
            return new(true, result, warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct())) { IsPartial = warnings.Count != 0 };
        }
        finally { _host.Call("ts_tree_delete", tree); }
    }

    private static CodeCapture Convert(TreeSitterQuery.Capture capture, int offset, bool injected = false) => new(
        checked(capture.Node.Start + offset), checked(capture.End + offset), capture.Name,
        new(HasConceal: capture.Pattern.Properties.TryGetValue("conceal", out var conceal) && (!injected || conceal is not null),
            Conceal: capture.Pattern.Properties.GetValueOrDefault("conceal"),
            // Offset captures carry _injectedQuery but not setProperties in the
            // source. Its nullish fallback drops null injected property values.
            ConcealLines: capture.Pattern.Properties.TryGetValue("conceal_lines", out var lines) && (!injected || lines is not null)));

    private int Parse(Parser parser, string content, CancellationToken cancellation)
    {
        _host!.Begin(content, cancellation);
        _host.Call("ts_parser_reset", parser.Handle);
        var tree = _host.Call("ts_parser_parse_wasm", parser.Handle, parser.Input, 0, 0, 0);
        if (tree != 0) return tree;
        cancellation.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Tree-sitter returned no syntax tree.");
    }

    private async Task<Parser> GetParser(string name, CancellationToken cancellation)
    {
        if (_parsers.TryGetValue(name, out var parser)) return parser;
        var registration = _snapshot.Registrations.GetValueOrDefault(name);
        if (registration is not null && _cache is null) throw new InvalidOperationException("Registered grammar loading requires an explicit asset cache.");
        var wasm = registration is null ? null : await _cache!.ReadAsync(registration.Wasm, 64 * 1024 * 1024, cancellation).ConfigureAwait(false);
        var highlightText = registration is null ? TreeSitterAssets.Query(name, "highlights") :
            await _cache!.QueriesAsync(registration.Highlights, cancellation).ConfigureAwait(false);
        var injectionText = registration is null ? (name == "markdown" ? TreeSitterAssets.Query(name, "injections") : "") :
            await _cache!.QueriesAsync(registration.Injections ?? [], cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(highlightText)) throw new InvalidDataException("Grammar has no nonempty highlight queries.");
        cancellation.ThrowIfCancellationRequested();
        var language = _host!.Language(name, wasm, registration?.LanguageExport);
        _host.Call("ts_parser_new_wasm");
        var handle = _host.Memory.ReadInt32(_host.Transfer);
        var input = _host.Memory.ReadInt32(_host.Transfer + 4);
        TreeSitterQuery? highlights = null;
        TreeSitterQuery? injections = null;
        try
        {
            if (handle == 0 || input == 0) throw new InvalidOperationException("Tree-sitter parser allocation failed.");
            if (_host.Call("ts_parser_set_language", handle, language) == 0) throw new InvalidOperationException("Tree-sitter rejected its grammar.");
            highlights = new(_host, language, highlightText);
            if (!string.IsNullOrWhiteSpace(injectionText)) injections = new(_host, language, injectionText);
            parser = new(handle, input, language, highlights, injections);
            _parsers.Add(name, parser);
            return parser;
        }
        catch
        {
            injections?.Dispose();
            highlights?.Dispose();
            if (handle != 0) _host.Call("ts_parser_delete", handle);
            if (input != 0) _host.Call("free", input);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            DisposeRuntime();
        }
        finally { _gate.Release(); }
    }

    private void DisposeRuntime()
    {
        if (_host is null) return;
        try
        {
            foreach (var parser in _parsers.Values)
            {
                parser.Injections?.Dispose();
                parser.Highlights.Dispose();
                _host.Call("ts_parser_delete", parser.Handle);
                _host.Call("free", parser.Input);
            }
        }
        finally { _parsers.Clear(); _host.Dispose(); _host = null; }
    }

    private sealed record Parser(int Handle, int Input, int Language, TreeSitterQuery Highlights, TreeSitterQuery? Injections);
}
