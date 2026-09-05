namespace OpenTui.Blazor.Code;

using System.Text;
using System.Text.RegularExpressions;

internal sealed class TreeSitterQuery : IDisposable
{
    internal sealed record Step(bool Capture, string Value);
    internal sealed record Pattern(IReadOnlyList<Step[]> Predicates, IReadOnlyDictionary<string, string?> Properties);
    internal sealed record Capture(string Name, WasmNode Node, int End, Pattern Pattern);
    private readonly TreeSitterWasm _host;
    private readonly int _query;
    private readonly string[] _names;
    private readonly Pattern[] _patterns;
    private readonly Dictionary<string, Regex> _regex = new(StringComparer.Ordinal);

    internal TreeSitterQuery(TreeSitterWasm host, int language, string source)
    {
        _host = host;
        var text = host.Utf8(source);
        try { _query = host.Call("ts_query_new", language, text, Encoding.UTF8.GetByteCount(source), host.Transfer, host.Transfer + 4); }
        finally { host.Call("free", text); }
        if (_query == 0) throw new InvalidDataException($"Tree-sitter query error {host.Memory.ReadInt32(host.Transfer + 4)} at byte {host.Memory.ReadInt32(host.Transfer)}.");
        try
        {
            _names = Names("ts_query_capture_count", "ts_query_capture_name_for_id");
            var strings = Names("ts_query_string_count", "ts_query_string_value_for_id");
            _patterns = Enumerable.Range(0, host.Call("ts_query_pattern_count", _query)).Select(index =>
            {
                var address = host.Call("ts_query_predicates_for_pattern", _query, index, host.Transfer);
                var count = host.Memory.ReadInt32(host.Transfer);
                var predicates = new List<Step[]>();
                var properties = new Dictionary<string, string?>();
                var steps = new List<Step>();
                for (var offset = 0; offset < count; offset++)
                {
                    var type = host.Memory.ReadInt32(address + offset * 8);
                    var value = host.Memory.ReadInt32(address + offset * 8 + 4);
                    if (type != 0)
                    {
                        steps.Add(new(type == 1, type == 1 ? _names[value] : strings[value]));
                        continue;
                    }
                    if (steps.Count == 0) continue;
                    if (steps[0].Capture) throw new InvalidDataException("Query predicate operator must be a literal.");
                    if (steps[0].Value == "set!")
                    {
                        if (steps.Count is < 2 or > 3 || steps.Any(step => step.Capture)) throw new InvalidDataException("Invalid #set! query directive.");
                        properties[steps[1].Value] = steps.Count == 3 ? steps[2].Value : null;
                    }
                    else predicates.Add(steps.ToArray());
                    steps.Clear();
                }
                return new Pattern(predicates, properties);
            }).ToArray();
        }
        catch { host.Call("ts_query_delete", _query); throw; }
    }

    private string[] Names(string countFunction, string nameFunction) => Enumerable.Range(0, _host.Call(countFunction, _query)).Select(index =>
    {
        var address = _host.Call(nameFunction, _query, index, _host.Transfer);
        return _host.Memory.ReadString(address, _host.Memory.ReadInt32(_host.Transfer));
    }).ToArray();

    internal IReadOnlyList<Capture> Captures(int tree, string content, CancellationToken cancellation)
    {
        _host.Call("ts_tree_root_node_wasm", tree);
        _host.Call("ts_query_captures_wasm", _query, tree, 0, 0, 0, 0, 0, 0, uint.MaxValue, uint.MaxValue, 0);
        var count = _host.Memory.ReadInt32(_host.Transfer);
        var allocation = _host.Memory.ReadInt32(_host.Transfer + 4);
        var exceeded = _host.Memory.ReadInt32(_host.Transfer + 8) != 0;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (exceeded) throw new InvalidOperationException("Tree-sitter query match limit exceeded.");
            var address = allocation;
            var result = new List<Capture>();
            for (var index = 0; index < count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var pattern = _patterns[_host.Memory.ReadInt32(address)];
                var captureCount = _host.Memory.ReadInt32(address + 4);
                var selected = _host.Memory.ReadInt32(address + 8);
                address += 12;
                var captures = new Capture[captureCount];
                for (var capture = 0; capture < captureCount; capture++)
                {
                    var name = _names[_host.Memory.ReadInt32(address)];
                    var node = WasmNode.Read(_host, address + 4);
                    node.Write(_host);
                    captures[capture] = new(name, node, _host.Call("ts_node_end_index_wasm", tree), pattern);
                    address += 24;
                }
                if (pattern.Predicates.All(predicate => Evaluate(predicate, captures, content))) result.Add(captures[selected]);
            }
            return result;
        }
        finally { _host.Call("free", allocation); }
    }

    // Regex is used only for an actual Tree-sitter #match? predicate, never to discover tokens.
    private bool Evaluate(Step[] steps, Capture[] captures, string content)
    {
        var op = steps[0].Value;
        if (op is "set!" or "is?" or "is-not?") return true;
        var equality = op is "eq?" or "not-eq?" or "any-eq?" or "any-not-eq?";
        var match = op is "match?" or "not-match?" or "any-match?" or "any-not-match?";
        var anyOf = op is "any-of?" or "not-any-of?";
        // web-tree-sitter retains unrecognized directives for consumers; the OpenTUI
        // worker does not execute them (including lua-match? and offset!).
        if (!equality && !match && !anyOf) return true;
        if (steps.Length < 2 || !steps[1].Capture || (!anyOf && steps.Length != 3))
            throw new InvalidDataException($"Invalid #{op} predicate arguments.");
        var values = captures.Where(capture => capture.Name == steps[1].Value)
            .Select(capture => content[capture.Node.Start..capture.End]).ToArray();
        if (anyOf)
        {
            if (steps.Skip(2).Any(step => step.Capture)) throw new InvalidDataException("#any-of? requires string values.");
            var positive = op == "any-of?";
            return values.Length == 0 ? !positive : values.All(value => steps.Skip(2).Any(step => step.Value == value)) == positive;
        }
        var isPositive = op is "eq?" or "any-eq?" or "match?" or "any-match?";
        var all = !op.StartsWith("any-", StringComparison.Ordinal);
        if (match)
        {
            if (steps[2].Capture) throw new InvalidDataException("#match? requires a literal pattern.");
            if (values.Length == 0) return !isPositive;
            if (!_regex.TryGetValue(steps[2].Value, out var regex))
            {
                regex = new Regex(steps[2].Value, RegexOptions.ECMAScript | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                _regex.Add(steps[2].Value, regex);
            }
            return all ? values.All(value => regex.IsMatch(value) == isPositive) : values.Any(value => regex.IsMatch(value) == isPositive);
        }
        var compared = steps[2].Capture
            ? captures.Where(capture => capture.Name == steps[2].Value).Select(capture => content[capture.Node.Start..capture.End]).ToArray()
            : [steps[2].Value];
        return all ? values.All(value => compared.Any(other => (value == other) == isPositive))
            : values.Any(value => compared.Any(other => (value == other) == isPositive));
    }

    public void Dispose() => _host.Call("ts_query_delete", _query);
}

internal readonly record struct WasmNode(int Id, int Start, int Row, int Column, int Other)
{
    internal static WasmNode Read(TreeSitterWasm host, int address) => new(host.Memory.ReadInt32(address), host.Memory.ReadInt32(address + 4),
        host.Memory.ReadInt32(address + 8), host.Memory.ReadInt32(address + 12), host.Memory.ReadInt32(address + 16));
    internal void Write(TreeSitterWasm host)
    {
        host.Memory.WriteInt32(host.Transfer, Id);
        host.Memory.WriteInt32(host.Transfer + 4, Start);
        host.Memory.WriteInt32(host.Transfer + 8, Row);
        host.Memory.WriteInt32(host.Transfer + 12, Column);
        host.Memory.WriteInt32(host.Transfer + 16, Other);
    }
    internal string Type(TreeSitterWasm host, int tree, int language)
    {
        Write(host);
        var symbol = host.Call("ts_node_symbol_wasm", tree);
        return host.Memory.ReadNullTerminatedString(host.Call("ts_language_symbol_name", language, symbol));
    }
    internal WasmNode Parent(TreeSitterWasm host, int tree)
    {
        Write(host);
        host.Call("ts_node_parent_wasm", tree);
        return Read(host, host.Transfer);
    }
    internal WasmNode ChildOfType(TreeSitterWasm host, int tree, int language, string type)
    {
        Write(host);
        var count = host.Call("ts_node_child_count_wasm", tree);
        for (var index = 0; index < count; index++)
        {
            Write(host);
            host.Call("ts_node_child_wasm", tree, index);
            var child = Read(host, host.Transfer);
            if (child.Type(host, tree, language) == type) return child;
        }
        return default;
    }
}
