namespace OpenCode.Core.CodeMode;

using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private JsValue _mapConstructor = JsValue.Undefined;
    private JsValue _setConstructor = JsValue.Undefined;
    private ObjectInstance? _mapPrototype;
    private ObjectInstance? _setPrototype;
    private static readonly HashSet<string> MapMethods = Names("get set has delete clear forEach keys values entries");
    private static readonly HashSet<string> SetMethods = Names("add has delete clear forEach keys values entries union intersection difference symmetricDifference isSubsetOf isSupersetOf isDisjointFrom");

    private void InstallCollections()
    {
        _mapConstructor = engine.GetValue("Map");
        _setConstructor = engine.GetValue("Set");
        _mapPrototype = (ObjectInstance)_mapConstructor.Get("prototype");
        _setPrototype = (ObjectInstance)_setConstructor.Get("prototype");
        Static("Map", "groupBy");
        Static("Set", "");
        ((ObjectInstance)_mapConstructor).DefineOwnProperty("groupBy", new PropertyDescriptor(
            new ClrFunction(engine, "groupBy", (_, args) => Guest(() => GroupMap(args.ToArray()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_newMap", new PropertyDescriptor(new ClrFunction(engine, "newMap", (_, args) =>
            Guest(() => ConstructCollection(true, args.ToArray()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_newSet", new PropertyDescriptor(new ClrFunction(engine, "newSet", (_, args) =>
            Guest(() => ConstructCollection(false, args.ToArray()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_forIn", new PropertyDescriptor(new ClrFunction(engine, "forIn", (_, args) => Guest(() =>
        {
            var value = args[0];
            if (_tools.ContainsKey(value)) return value;
            if (value is ArrayInstance || value is JsObject && !_statics.ContainsKey(value)) return value;
            return Unsupported("for...in requires a plain object, array, or tools reference; use for...of for collections.");
        })), PropertyFlag.AllForbidden));
    }

    private bool IsMap(JsValue value) => _mapPrototype is not null && value is ObjectInstance obj && ReferenceEquals(obj.Prototype, _mapPrototype);
    private bool IsSet(JsValue value) => _setPrototype is not null && value is ObjectInstance obj && ReferenceEquals(obj.Prototype, _setPrototype);
    private bool IsCollection(JsValue value) => IsMap(value) || IsSet(value);

    private JsValue NativeCollection(JsValue target, string method, params JsValue[] args) =>
        engine.Call((IsMap(target) ? _mapPrototype! : _setPrototype!).Get(method), target, args);

    private int CollectionSize(JsValue target) => (int)target.Get("size").AsNumber();

    private void CheckCollectionGrowth(JsValue target, JsValue key)
    {
        checkpoint();
        if (CollectionSize(target) >= Math.Min(limits.MaxBoundaryBytes, 100000) && !NativeCollection(target, "has", key).AsBoolean())
            Invalid("Collection exceeds the configured item ceiling.");
    }

    private JsValue ConstructCollection(bool map, JsValue[] args)
    {
        var result = engine.Construct(map ? _mapConstructor : _setConstructor);
        if (args.Length == 0 || args[0].IsUndefined() || args[0].IsNull()) return result;
        ConsumeIterable(args[0], item =>
        {
            checkpoint();
            if (!map)
            {
                CheckCollectionGrowth(result, item);
                NativeCollection(result, "add", item);
                return;
            }
            // The source accepts entry data objects, not just length-two arrays,
            // but never a tool/namespace/function used as an entry object.
            if (item is not ObjectInstance entry || _tools.ContainsKey(entry) || _statics.ContainsKey(entry) ||
                !(entry is ArrayInstance || entry is JsObject && (entry.Prototype is null || ReferenceEquals(entry.Prototype, engine.Intrinsics.Object.PrototypeObject))))
                throw new CodeModeDiagnosticException(new("ExecutionFailure", "new Map(...) expects [key, value] pairs as entry data objects."));
            var key = Read(entry, "0");
            CheckCollectionGrowth(result, key);
            NativeCollection(result, "set", key, Read(entry, "1"));
        });
        return result;
    }

    /// <summary>
    /// Only built-in synchronous iterables. Native iterator objects stay host-side;
    /// no arbitrary iterator/getter/CLR capability is called to discover a cursor.
    /// Each yield checks the same invocation budget, including callback-driven growth.
    /// </summary>
    private IEnumerable<JsValue> IterateCollection(JsValue source, bool keys = false)
    {
        if (source is ArrayInstance array)
        {
            for (uint index = 0; index < Own(array, "length").AsNumber(); index++)
            {
                checkpoint();
                yield return Own(array, index.ToString(CultureInfo.InvariantCulture));
            }
            yield break;
        }
        if (source.IsString())
        {
            var text = source.AsString();
            for (var index = 0; index < text.Length; index++)
            {
                checkpoint();
                var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                yield return new JsString(text.Substring(index, length));
                index += length - 1;
            }
            yield break;
        }
        if (source is QueryValue query)
        {
            for (var index = 0; ; index++)
            {
                RefreshQuery(query);
                if (index >= query.Items.Count) yield break;
                var item = query.Items[index];
                yield return new JsArray(engine, new JsValue[] { new JsString(item.Key), new JsString(item.Value) });
            }
        }
        if (!IsCollection(source))
        {
            Unsupported("A supported synchronous iterable (array, string, Map, or Set) is required.");
            yield break;
        }
        var iterator = NativeCollection(source, keys ? "keys" : IsMap(source) ? "entries" : "values");
        var next = iterator.Get("next");
        while (true)
        {
            checkpoint();
            var step = engine.Call(next, iterator, Array.Empty<JsValue>());
            if (step.Get("done").AsBoolean()) yield break;
            yield return step.Get("value");
        }
    }

    private JsValue[] CollectionSnapshot(JsValue target, string method)
    {
        var items = new List<JsValue>();
        foreach (var item in IterateCollection(target, method == "keys"))
        {
            if (items.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Collection materialization exceeds the item ceiling.");
            items.Add(IsMap(target) && method == "values" ? Own((ObjectInstance)item, "1") :
                IsSet(target) && method == "entries" ? new JsArray(engine, new[] { item, item }) : item);
        }
        return items.ToArray();
    }

    private JsValue ReadCollection(JsValue target, string key)
    {
        if (key == "size") return new JsNumber(CollectionSize(target));
        if (!(IsMap(target) ? MapMethods : SetMethods).Contains(key)) return JsValue.Undefined;
        // Keep the original receiver when a method is detached, like IntrinsicReference.
        return new ClrFunction(engine, key, (_, args) => Guest(() => InvokeCollection(target, key, args.ToArray())));
    }

    private JsValue InvokeCollection(JsValue target, string method, JsValue[] args)
    {
        var first = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (method is "keys" or "values" or "entries") return new JsArray(engine, CollectionSnapshot(target, method));
        if (method == "forEach")
        {
            RequireCollectionCallback(first, IsMap(target) ? "Map.forEach" : "Set.forEach");
            // Deliberately snapshot, unlike native Map/Set.forEach. Source additions
            // made by callbacks are not visited, and deleted snapshot items still are.
            foreach (var item in CollectionSnapshot(target, IsMap(target) ? "entries" : "values"))
            {
                checkpoint();
                var key = IsMap(target) ? Own((ObjectInstance)item, "0") : item;
                var value = IsMap(target) ? Own((ObjectInstance)item, "1") : item;
                engine.Call(first, JsValue.Undefined, new[] { value, key, target });
            }
            return JsValue.Undefined;
        }
        if (method is "set" or "add") CheckCollectionGrowth(target, first);
        if (method is "get" or "has" or "set" or "add" or "delete" or "clear")
            return NativeCollection(target, method, args);
        return SetOperation(target, method, first);
    }

    private void RequireCollectionCallback(JsValue value, string name)
    {
        if (value is not Function || _tools.ContainsKey(value) || _jsonRejectedCallbacks.Contains(value))
            Unsupported(name + " requires a supported callback; wrap tool or Promise-method calls in an arrow function.");
    }

    private JsValue GroupMap(JsValue[] args)
    {
        if (args.Length < 2) return Unsupported("Map.groupBy expects an iterable and function callback.");
        RequireCollectionCallback(args[1], "Map.groupBy");
        var result = engine.Construct(_mapConstructor);
        var index = 0;
        ConsumeIterable(args[0], item =>
        {
            checkpoint();
            var key = engine.Call(args[1], JsValue.Undefined, new[] { item, (JsValue)new JsNumber(index++) });
            var group = NativeCollection(result, "get", key);
            if (group.IsUndefined())
            {
                CheckCollectionGrowth(result, key);
                NativeCollection(result, "set", key, new JsArray(engine, new[] { item }));
            }
            else
            {
                if (Own((ObjectInstance)group, "length").AsNumber() >= Math.Min(limits.MaxBoundaryBytes, 100000))
                    Invalid("Map.groupBy bucket exceeds the configured item ceiling.");
                engine.Call(engine.Intrinsics.Array.PrototypeObject.Get("push"), group, new[] { item });
            }
        });
        return result;
    }

    private JsValue SetOperation(JsValue target, string name, JsValue other)
    {
        var record = SetRecord(other, name);
        var leftSize = CollectionSize(target);
        var rightSize = record.Size;
        bool Has(JsValue set, JsValue item) { checkpoint(); return NativeCollection(set, "has", item).AsBoolean(); }
        if (name == "isSubsetOf")
            return leftSize <= rightSize && IterateCollection(target).All(record.Has) ? JsBoolean.True : JsBoolean.False;
        if (name == "isSupersetOf")
            return leftSize >= rightSize && record.Keys().All(item => Has(target, item)) ? JsBoolean.True : JsBoolean.False;
        if (name == "isDisjointFrom")
            return (leftSize <= rightSize ? IterateCollection(target).All(item => !record.Has(item)) : record.Keys().All(item => !Has(target, item))) ? JsBoolean.True : JsBoolean.False;
        var result = engine.Construct(_setConstructor);
        void Add(JsValue item) { CheckCollectionGrowth(result, item); NativeCollection(result, "add", item); }
        if (name == "intersection")
        {
            foreach (var item in leftSize <= rightSize ? IterateCollection(target) : record.Keys())
                if (leftSize <= rightSize ? record.Has(item) : Has(target, item)) Add(item);
            return result;
        }
        foreach (var item in IterateCollection(target)) Add(item);
        if (name == "union")
        {
            foreach (var item in record.Keys()) Add(item);
            return result;
        }
        if (name == "difference")
        {
            foreach (var item in leftSize <= rightSize ? IterateCollection(result) : record.Keys())
                if (leftSize > rightSize || record.Has(item)) NativeCollection(result, "delete", item);
            return result;
        }
        if (name == "symmetricDifference")
        {
            foreach (var item in record.Keys())
                if (Has(target, item)) NativeCollection(result, "delete", item);
                else Add(item);
            return result;
        }
        return Unsupported("Unsupported Set operation: " + name);
    }

    private (double Size, Func<JsValue, bool> Has, Func<IEnumerable<JsValue>> Keys) SetRecord(JsValue source, string name)
    {
        if (IsCollection(source)) return (CollectionSize(source), item =>
        {
            checkpoint();
            return NativeCollection(source, "has", item).AsBoolean();
        }, () => IterateCollection(source, true));
        if (source is not JsObject obj || _tools.ContainsKey(obj) || _statics.ContainsKey(obj))
            throw new CodeModeDiagnosticException(new("ExecutionFailure", "Set." + name + " expects a Set-like data object."));
        var size = ValueNumber(DatePrimitive(Read(obj, "size"), numeric: true));
        if (double.IsNaN(size)) throw new CodeModeDiagnosticException(new("ExecutionFailure", "Set-like size is invalid."));
        var has = Read(obj, "has");
        var keys = Read(obj, "keys");
        RequireCollectionCallback(has, "Set-like has");
        RequireCollectionCallback(keys, "Set-like keys");
        return (Math.Max(Math.Truncate(size), 0), item =>
        {
            checkpoint();
            var value = engine.Call(has, JsValue.Undefined, new[] { item });
            return !value.IsNull() && !value.IsUndefined() && (value.IsBoolean() ? value.AsBoolean() : value.IsString() ? value.AsString().Length > 0 :
                !value.IsNumber() || value.AsNumber() != 0 && !double.IsNaN(value.AsNumber()));
        }, () =>
        {
            checkpoint();
            var value = engine.Call(keys, JsValue.Undefined, Array.Empty<JsValue>());
            if (value is not ArrayInstance) throw new CodeModeDiagnosticException(new("ExecutionFailure", "Set-like keys must return an array in this runtime."));
            return IterateCollection(value);
        });
    }
}
