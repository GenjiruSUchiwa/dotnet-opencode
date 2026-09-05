namespace OpenTui.Blazor.Code;

using System.Diagnostics;
using System.Text;
using Wasmtime;

/// <summary>Private store for the pinned web-tree-sitter ABI. Access is serialized by its highlighter.</summary>
internal sealed class TreeSitterWasm : IDisposable
{
    private readonly Engine _engine;
    private readonly TimeProvider _clock;
    private readonly Store _store;
    private readonly Memory _memory;
    private readonly Table _table;
    private readonly Global _stack;
    private readonly Dictionary<string, Function> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Function> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _languages = new(StringComparer.Ordinal);
    private string _content = "";
    private CancellationToken _cancellation;
    internal int Transfer { get; private set; }
    internal Memory Memory => _memory;

    private TreeSitterWasm(Engine engine, Store store, TimeProvider clock)
    {
        _clock = clock;
        _engine = engine;
        _store = store;
        _memory = new(store, minimum: 512, maximum: 8192);
        _table = new(store, TableKind.FuncRef, null!, 1, 1_000_000);
        // These bootstrap addresses are part of web-tree-sitter 0.25.10's pinned JS binding.
        _stack = new(store, ValueKind.Int32, 78224, Mutability.Mutable);
    }

    internal static TreeSitterWasm Create(TimeProvider clock)
    {
        var engine = new Engine();
        Store? store = null;
        TreeSitterWasm? host = null;
        try
        {
            store = new Store(engine);
            host = new(engine, store, clock);
            host.DefineCallbacks();
            var bytes = TreeSitterAssets.Read("tree-sitter.wasm");
            var metadata = WasmDylink.Read(bytes);
            host._table.Grow(checked((uint)metadata.TableSize), null!);
            host.Load("tree-sitter", bytes, 1024, 1, runtime: true);
            host.Transfer = host.Call("ts_init");
            return host;
        }
        catch
        {
            try { store?.Dispose(); }
            finally { engine.Dispose(); }
            throw;
        }
    }

    internal void Begin(string content, CancellationToken cancellation)
    {
        _content = content;
        _cancellation = cancellation;
        cancellation.ThrowIfCancellationRequested();
    }

    internal int Language(string name, byte[]? wasm = null, string? languageExport = null)
    {
        if (_languages.TryGetValue(name, out var language)) return language;
        var bytes = wasm ?? TreeSitterAssets.Read($"{name}.tree-sitter-{name}.wasm");
        var metadata = WasmDylink.Read(bytes);
        if (metadata.MemoryAlignment > 24 || metadata.TableAlignment > 20)
            throw new InvalidDataException("Unsupported dylink alignment.");
        var alignment = 1 << metadata.MemoryAlignment;
        var allocation = Call("calloc", 1, checked(metadata.MemorySize + alignment));
        if (allocation == 0) throw new InvalidOperationException("Tree-sitter grammar allocation failed.");
        var memoryBase = checked((allocation + alignment - 1) & -alignment);
        var tableAlignment = 1u << metadata.TableAlignment;
        // Wasmtime returns ulong even for this wasm32 table. Narrow only after
        // checked arithmetic and enforce the same explicit limit as its constructor.
        var oldSize = _table.GetSize();
        var tableBase = checked(oldSize + tableAlignment - 1) & ~((ulong)tableAlignment - 1);
        var requiredSize = checked(tableBase + (ulong)metadata.TableSize);
        if (requiredSize > 1_000_000) throw new InvalidDataException("Tree-sitter grammar exceeds the wasm32 table limit.");
        _table.Grow(checked((uint)(requiredSize - oldSize)), null!);
        var instance = Load(name, bytes, memoryBase, checked((int)tableBase), runtime: false);
        var export = instance.GetFunction(languageExport ?? "tree_sitter_" + name)
            ?? throw new InvalidDataException("Registered grammar does not export its language function.");
        if (export.Parameters.Count != 0 || export.Results.Count != 1 || export.Results[0] != ValueKind.Int32)
            throw new InvalidDataException("Registered language export must have the wasm32 () -> i32 signature.");
        language = Convert.ToInt32(export.Invoke(), System.Globalization.CultureInfo.InvariantCulture);
        var version = Call("ts_language_version", language);
        // ts_init writes the supported language range into the transfer buffer.
        Call("ts_init");
        if (version > _memory.ReadInt32(Transfer) || version < _memory.ReadInt32(Transfer + 4))
            throw new NotSupportedException($"Unsupported Tree-sitter grammar ABI {version} for {name}.");
        _languages.Add(name, language);
        return language;
    }

    private Instance Load(string name, byte[] bytes, int memoryBase, int tableBase, bool runtime)
    {
        using var module = Module.FromBytes(_engine, name, bytes);
        using var linker = new Linker(_engine);
        foreach (var import in module.Imports)
        {
            switch (import)
            {
                case MemoryImport when import.ModuleName == "env" && import.Name == "memory":
                    linker.Define(import.ModuleName, import.Name, _memory);
                    break;
                case TableImport when import.ModuleName == "env" && import.Name == "__indirect_function_table":
                    linker.Define(import.ModuleName, import.Name, _table);
                    break;
                case GlobalImport when import.ModuleName == "env" && import.Name == "__stack_pointer":
                    linker.Define(import.ModuleName, import.Name, _stack);
                    break;
                case GlobalImport when import.ModuleName == "env" && import.Name is "__memory_base" or "__table_base":
                    linker.Define(import.ModuleName, import.Name, new Global(_store, ValueKind.Int32,
                        import.Name == "__memory_base" ? memoryBase : tableBase, Mutability.Immutable));
                    break;
                case GlobalImport when runtime && import.ModuleName == "GOT.mem" && import.Name == "__heap_base":
                    linker.Define(import.ModuleName, import.Name, new Global(_store, ValueKind.Int32, 78224, Mutability.Mutable));
                    break;
                case FunctionImport when _imports.TryGetValue(import.ModuleName + "." + import.Name, out var callback):
                    linker.Define(import.ModuleName, import.Name, callback);
                    break;
                case FunctionImport when import.ModuleName == "env" && _functions.TryGetValue(import.Name, out var function):
                    linker.Define(import.ModuleName, import.Name, function);
                    break;
                default:
                    throw new NotSupportedException($"Unapproved Tree-sitter WASM import {import.ModuleName}.{import.Name}.");
            }
        }
        var instance = linker.Instantiate(_store, module);
        foreach (var export in module.Exports.OfType<FunctionExport>())
        {
            if (export.Name.StartsWith("__", StringComparison.Ordinal)) continue;
            _functions.TryAdd(export.Name, instance.GetFunction(export.Name)!);
        }
        // Apply shared-memory relocations before constructors, as in loadWebAssemblyModule.
        instance.GetFunction("__wasm_apply_data_relocs")?.Invoke();
        instance.GetFunction("__wasm_call_ctors")?.Invoke();
        return instance;
    }

    internal int Call(string name, params long[] arguments)
    {
        var function = _functions[name];
        if (function.Parameters.Count != arguments.Length) throw new InvalidDataException($"Incorrect Tree-sitter ABI arity for {name}.");
        var values = arguments.Select((value, index) => function.Parameters[index] switch
        {
            // The ABI accepts wasm32 bit patterns (including UINT32_MAX for
            // query limits), not arbitrary truncated 64-bit addresses.
            ValueKind.Int32 when value is >= int.MinValue and <= uint.MaxValue => (ValueBox)unchecked((int)value),
            ValueKind.Int64 => (ValueBox)value,
            ValueKind.Float64 => (ValueBox)(double)value,
            _ => throw new NotSupportedException($"Unsupported Tree-sitter ABI parameter in {name}."),
        }).ToArray();
        var result = function.Invoke(values);
        return result is null ? 0 : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal int Utf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var pointer = Call("malloc", checked(bytes.Length + 1));
        if (pointer == 0) throw new InvalidOperationException("Tree-sitter string allocation failed.");
        bytes.CopyTo(_memory.GetSpan(pointer, bytes.Length));
        _memory.WriteByte(pointer + bytes.Length, 0);
        return pointer;
    }

    private void DefineCallbacks()
    {
        _imports.Add("env.tree_sitter_parse_callback", Function.FromCallback(_store,
            (Action<int, int, int, int, int>)((buffer, index, row, column, length) =>
            {
                var count = index < 0 || index >= _content.Length || _cancellation.IsCancellationRequested
                    ? 0 : Math.Min(5119, _content.Length - index);
                // Match stringToUTF16: copy code units, including a surrogate at
                // a chunk boundary. Encoding.Unicode would replace unmatched units.
                for (var unit = 0; unit < count; unit++)
                    _memory.WriteInt16(buffer + unit * 2, unchecked((short)_content[index + unit]));
                _memory.WriteInt16(buffer + count * 2, 0);
                _memory.WriteInt32(length, count);
            })));
        _imports.Add("env.tree_sitter_progress_callback", Function.FromCallback(_store,
            (Func<int, int, int>)((offset, error) => _cancellation.IsCancellationRequested ? 1 : 0)));
        _imports.Add("env.tree_sitter_query_progress_callback", Function.FromCallback(_store,
            (Func<int, int>)(offset => _cancellation.IsCancellationRequested ? 1 : 0)));
        _imports.Add("env.tree_sitter_log_callback", Function.FromCallback(_store, (Action<int, int>)((kind, text) => { })));
        _imports.Add("env._abort_js", Function.FromCallback(_store, (Action)(() => throw new InvalidOperationException("Tree-sitter WASM aborted."))));
        // Statically verified () -> () import in the production Kotlin, XML and YAML grammars.
        _imports.Add("env.abort", Function.FromCallback(_store, (Action)(() => throw new InvalidOperationException("Tree-sitter grammar aborted."))));
        _imports.Add("env.__assert_fail", Function.FromCallback(_store, (Action<int, int, int, int>)((condition, file, line, function) =>
            throw new InvalidOperationException("Tree-sitter grammar assertion failed."))));
        _imports.Add("env.emscripten_resize_heap", Function.FromCallback(_store, (Func<int, int>)(requested =>
        {
            var target = (uint)requested;
            if (target > 512 * 1024 * 1024) return 0;
            var delta = ((long)target + Memory.PageSize - 1) / Memory.PageSize - _memory.GetSize();
            if (delta > 0) _memory.Grow(delta);
            return 1;
        })));
        // No WASI context, descriptors, preopened directories, environment, or network is granted.
        _imports.Add("wasi_snapshot_preview1.fd_close", Function.FromCallback(_store, (Func<int, int>)(fd => 8)));
        _imports.Add("wasi_snapshot_preview1.fd_seek", Function.FromCallback(_store, (Func<int, long, int, int, int>)((fd, offset, whence, result) => 8)));
        _imports.Add("wasi_snapshot_preview1.fd_write", Function.FromCallback(_store, (Func<int, int, int, int, int>)((fd, vectors, count, written) => 8)));
        _imports.Add("wasi_snapshot_preview1.clock_time_get", Function.FromCallback(_store, (Func<int, long, int, int>)((clock, precision, pointer) =>
        {
            if (clock is not (0 or 1)) return 28;
            var nanos = clock == 0 ? _clock.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000 :
                (long)(_clock.GetTimestamp() * (1_000_000_000d / _clock.TimestampFrequency));
            _memory.WriteInt64(pointer, nanos);
            return 0;
        })));
    }

    public void Dispose()
    {
        _functions.Clear();
        _imports.Clear();
        _store.Dispose();
        _engine.Dispose();
    }
}
