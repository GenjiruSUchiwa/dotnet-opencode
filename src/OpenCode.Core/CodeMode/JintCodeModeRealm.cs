namespace OpenCode.Core.CodeMode;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm(Engine engine, ICodeModeBindings bindings, CodeModeLimits limits, Action checkpoint,
    CodeModeErrorValues errors, CodeModePromiseObservations promises,
    Func<IReadOnlyList<string>, IReadOnlyList<JsonElement>, JsValue> call)
{
    private readonly Dictionary<JsValue, string[]> _tools = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, JsValue> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<JsValue, HashSet<string>> _statics = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<JsValue> _errors = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> ArrayMethods = Names("at concat every filter find findIndex findLast findLastIndex flat flatMap forEach includes indexOf join lastIndexOf map reduce reduceRight slice some toReversed toSorted toSpliced with");
    private static readonly HashSet<string> StringMethods = Names("at charAt charCodeAt codePointAt concat endsWith includes indexOf lastIndexOf localeCompare normalize padEnd padStart repeat replace replaceAll match matchAll search slice split startsWith substring toLowerCase toUpperCase trim trimStart trimEnd toString");
    private static readonly HashSet<string> NumberMethods = Names("toFixed toPrecision toExponential toString");
    private static readonly HashSet<string> PromiseMethods = Names("then catch finally");

    internal void Install()
    {
        _iteratorSymbol = engine.GetValue("Symbol").Get("iterator");
        _asyncIteratorSymbol = engine.GetValue("Symbol").Get("asyncIterator");
        _toPrimitiveSymbol = engine.GetValue("Symbol").Get("toPrimitive");
        var allowed = Names("undefined NaN Infinity Object Array Math JSON Number String Boolean parseInt parseFloat isFinite isNaN encodeURI encodeURIComponent decodeURI decodeURIComponent Promise Map Set Date RegExp Error TypeError RangeError SyntaxError ReferenceError EvalError URIError AggregateError");
        foreach (var key in engine.Global.GetOwnPropertyKeys())
            if (!key.IsString() || !allowed.Contains(key.AsString())) engine.Global.RemoveOwnProperty(key);
        Static("Object", "keys values entries hasOwn is");
        Static("Array", "isArray of from");
        Static("Math", "E LN2 LN10 LOG2E LOG10E PI SQRT1_2 SQRT2 abs acos acosh asin asinh atan atanh atan2 cbrt ceil clz32 cos cosh exp expm1 floor fround f16round hypot imul log log1p log2 log10 max min pow random round sign sin sinh sqrt sumPrecise tan tanh trunc");
        Static("Number", "EPSILON MAX_SAFE_INTEGER MAX_VALUE MIN_SAFE_INTEGER MIN_VALUE NaN NEGATIVE_INFINITY POSITIVE_INFINITY isFinite isInteger isNaN isSafeInteger parseFloat parseInt");
        Static("String", "fromCharCode fromCodePoint");
        Static("Promise", "all allSettled race any resolve reject");
        InstallCollections();
        InstallPrimitives();
        InstallUrls();
        InstallMemberWrites();
        foreach (var name in Names("Error TypeError RangeError SyntaxError ReferenceError EvalError URIError AggregateError"))
        {
            var prototype = ((ObjectInstance)engine.GetValue(name)).Get("prototype");
            _errors.Add(prototype);
            errors.RegisterNative(prototype, name);
        }
        InstallErrors();
        engine.SetValue("tools", Tool([]));
        engine.SetValue("search", new ClrFunction(engine, "search", (_, args) => Guest(() =>
            FromJson(engine, bindings.Search(args.ToArray().Select(value => ToJson(value, false)).ToArray())))));

        InstallJson();
        InstallIterators();
        InstallPromiseBoundary();
        InstallGenerators();
        InstallPatterns();
        InstallPatternScopes();
        InstallBindings();
        InstallCells();
        InstallSyntaxGuards();
        InstallDataViews();

        var console = new JsObject(engine);
        foreach (var name in Names("log info debug warn error dir table"))
            console.Set(name, new ClrFunction(engine, name, (_, args) => Guest(() =>
            {
                bindings.Log(string.Join(" ", args.ToArray().Select(value => value.IsString() ? value.AsString() : ToJson(value, true).GetRawText())));
                return JsValue.Undefined;
            })), false);
        engine.SetValue("console", console);
        _statics[console] = Names("log info debug warn error dir table");
        foreach (var name in allowed.Concat(new[] { "tools", "search", "console", "URL", "URLSearchParams", "Symbol" }))
            engine.Global.DefineOwnProperty(name, new PropertyDescriptor(engine.GetValue(name), PropertyFlag.AllForbidden));
    }

    private void Static(string name, string members) => _statics[engine.GetValue(name)] = Names(members);
    private ObjectInstance Prototype(string name) => (ObjectInstance)engine.GetValue(name).Get("prototype");
    private static HashSet<string> Names(string names) => names.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private JsValue Tool(string[] path)
    {
        var key = string.Join(".", path);
        if (_paths.TryGetValue(key, out var existing)) return existing;
        JsValue value = path.Length == 0 ? new JsObject(engine) : new ClrFunction(engine, key, (_, args) =>
        {
            var ordinal = promises.Reserve();
            try { return promises.Track(call(path, args.ToArray().Select(value => ToJson(value, false)).ToArray()), ordinal); }
            catch (CodeModeDiagnosticException error)
            { return promises.Track(engine.Call(_nativePromiseMethods["reject"], _promiseConstructor, new[] { errors.Runtime(error.Diagnostic) }), ordinal); }
        });
        _paths.Add(key, value);
        _tools.Add(value, path);
        // Enumeration sees captured child names, but native promise assimilation
        // must not turn a tool named "then" into an implicit host invocation.
        // Explicit guest member reads resolve through Read, not these inert values.
        try
        {
            foreach (var child in bindings.Keys(path))
                ((ObjectInstance)value).DefineOwnProperty(child, new PropertyDescriptor(JsValue.Undefined, PropertyFlag.Enumerable));
        }
        catch (CodeModeDiagnosticException error) when (error.Diagnostic.Kind == "UnknownTool")
        {
            // A path reference is inert. Resolve availability when called; explicit
            // namespace enumeration still reports the original lookup error.
        }
        return value;
    }

    internal JsValue Read(JsValue target, JsValue property)
    {
        checkpoint();
        if (IteratorKey(property))
        {
            if (target is GeneratorValue generator)
                return ReferenceEquals(property, generator.Asynchronous ? _asyncIteratorSymbol : _iteratorSymbol)
                    ? new ClrFunction(engine, "iterator", (_, _) => generator) : JsValue.Undefined;
            if (_tools.ContainsKey(target)) return Guest(() => Unsupported("Tool paths must use string property names."));
            if (target is JsObject data && PlainWritable(data)) return IteratorReadValue(Own(data, property));
            if (IsBuiltinValue(target) || target is ArrayInstance) return JsValue.Undefined;
            return Guest(() => Unsupported("Runtime references are opaque."));
        }
        if (!property.IsString() && !property.IsNumber()) return Guest(() => Unsupported("Symbol property access is unavailable."));
        var key = property.IsString() ? property.AsString() : property.AsNumber().ToString(CultureInfo.InvariantCulture);
        if (_tools.TryGetValue(target, out var path)) return Guest(() => Tool([.. path, key]));
        if (target is GeneratorValue generatorValue) return ReadGenerator(generatorValue, key);
        if (key is "constructor" or "prototype" or "__proto__") return Guest(() => Unsupported($"Property '{key}' is not available."));
        if (IsCollection(target)) return Guest(() => ReadCollection(target, key));
        if (IsDate(target)) return Guest(() => ReadDate(target, key));
        if (IsRegex(target)) return Guest(() => ReadRegex(target, key));
        if (IsUrlValue(target)) return Guest(() => ReadUrl(target, key));
        if (_statics.TryGetValue(target, out var members))
        {
            if (!members.Contains(key)) return JsValue.Undefined;
            var member = target.Get(property);
            if (ReferenceEquals(target, engine.GetValue("Array")) && key == "from")
                return new ClrFunction(engine, "from", (_, args) => Guest(() =>
                {
                    if (args.Length > 0 && args[0] is GeneratorValue { Asynchronous: true }) return Unsupported("Array.from requires a synchronous iterable.");
                    if (args.Length > 1 && !args[1].IsUndefined()) RequireCollectionCallback(args[1], "Array.from");
                    return engine.Call(member, target, args);
                }));
            if (target == engine.GetValue("Object") && key is "keys" or "values" or "entries")
                return new ClrFunction(engine, key, (_, args) => Guest(() =>
                {
                    if (args.Length > 0 && args[0] is GeneratorValue) return Unsupported("Generator references are opaque.");
                    if (args.Length > 0 && _tools.TryGetValue(args[0], out var namespacePath))
                    {
                        if (key != "keys") return Unsupported("Only Object.keys is supported for tool references.");
                        return new JsArray(engine, bindings.Keys(namespacePath).Select(item => (JsValue)new JsString(item)).ToArray());
                    }
                    return engine.Call(member, target, args);
                }));
            return member;
        }
        if (target.IsNull() || target.IsUndefined()) return Guest(() => Unsupported("Cannot read a property of null or undefined."));
        if (target.IsString())
        {
            if (key == "length") return new JsNumber(target.AsString().Length);
            if (Index(key) is { } index) return index < target.AsString().Length ? new JsString(target.AsString()[(int)index].ToString()) : JsValue.Undefined;
            if (key is "match" or "matchAll" or "search" or "replace" or "replaceAll" or "split") return ReadRegexString(target, key);
            return StringMethods.Contains(key) ? Bound(Prototype("String").Get(property), target) : JsValue.Undefined;
        }
        if (target.IsNumber()) return NumberMethods.Contains(key) ? Bound(Prototype("Number").Get(property), target) : JsValue.Undefined;
        if (target is not ObjectInstance obj) return JsValue.Undefined;
        if (obj is ArrayInstance)
        {
            if (key == "length" || Index(key) is not null || obj.HasOwnProperty(property)) return Own(obj, property);
            return ArrayMethods.Contains(key) ? Bound(engine.Intrinsics.Array.PrototypeObject.Get(property), obj) : JsValue.Undefined;
        }
        if (ReferenceEquals(obj.Prototype, Prototype("Promise")))
            return ReadPromise(obj, key);
        if (obj is Function) return Guest(() => Unsupported("Function member access is unavailable."));
        if (_errors.Contains(obj.Prototype!)) return key is "name" or "message" or "errors" ? obj.Get(property) : JsValue.Undefined;
        return Own(obj, property);
    }

    private JsValue Bound(JsValue function, JsValue receiver) => new ClrFunction(engine, "bound", (_, args) => Guest(() =>
    {
        if (args.ToArray().Any(value => _tools.ContainsKey(value)))
            return Unsupported("Tool references cannot be passed as callbacks; wrap the call in an arrow function.");
        return engine.Call(function, receiver, args);
    }));

    private static JsValue Own(ObjectInstance obj, JsValue key)
    {
        var descriptor = obj.GetOwnProperty(key);
        if (descriptor == PropertyDescriptor.Undefined) return JsValue.Undefined;
        if (!descriptor.IsDataDescriptor()) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Accessor properties are unavailable."));
        return descriptor.Value ?? JsValue.Undefined;
    }

    private static uint? Index(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        key == index.ToString(CultureInfo.InvariantCulture) && index < uint.MaxValue ? index : null;

    private JsValue Guest(Func<JsValue> operation)
    {
        try { checkpoint(); return operation(); }
        catch (CodeModeDiagnosticException error) { throw new JavaScriptException(Error(engine, error.Diagnostic)); }
    }
    private static JsValue Unsupported(string message) => throw new CodeModeDiagnosticException(new("UnsupportedSyntax", message));
    private static void Invalid(string message) => throw new CodeModeDiagnosticException(new("InvalidDataValue", message));

    internal JsValue Error(Engine engine, CodeModeDiagnostic diagnostic, string name = "Error") => errors.Runtime(diagnostic, name);

    internal static JsValue FromJson(Engine engine, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => JsValue.Null,
        JsonValueKind.True => JsBoolean.True,
        JsonValueKind.False => JsBoolean.False,
        JsonValueKind.Number => new JsNumber(value.GetDouble()),
        JsonValueKind.String => new JsString(value.GetString()!),
        JsonValueKind.Array => new JsArray(engine, value.EnumerateArray().Select(item => FromJson(engine, item)).ToArray()),
        JsonValueKind.Object => JsObject.CreateFromEntries(engine, value.EnumerateObject().Select(property =>
            new KeyValuePair<string, JsValue>(property.Name, FromJson(engine, property.Value)))),
        _ => throw new CodeModeDiagnosticException(new("InvalidDataValue", "Only JSON data may enter the program."))
    };

    internal JsonElement ToJson(JsValue value, bool nullify)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value, nullify, new HashSet<JsValue>(ReferenceEqualityComparer.Instance), 0);
        using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 33 });
        return document.RootElement.Clone();
    }

    private void Write(Utf8JsonWriter writer, JsValue value, bool nullify, HashSet<JsValue> seen, int depth)
    {
        checkpoint();
        value = JsonPrimitive(value);
        if (depth > 32) Invalid("Data exceeds the maximum value depth of 32.");
        if (value.IsUndefined() || value.IsNull()) writer.WriteNullValue();
        else if (value.IsBoolean()) writer.WriteBooleanValue(value.AsBoolean());
        else if (value.IsNumber())
        {
            if (double.IsFinite(value.AsNumber())) writer.WriteNumberValue(value.AsNumber());
            else writer.WriteNullValue();
        }
        else if (value.IsString())
        {
            if (Encoding.UTF8.GetByteCount(value.AsString()) > limits.MaxBoundaryBytes) Invalid("String exceeds the boundary byte limit.");
            writer.WriteStringValue(value.AsString());
        }
        else if (IsCollection(value) || IsRegex(value) || value is QueryValue)
        {
            // Source copyIn normalizes Map/Set to {} without traversing entries.
            // Their live contents and identities remain inside the invocation.
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        else if (value is ObjectInstance obj && obj is not Function && !_tools.ContainsKey(obj) &&
            (obj is ArrayInstance || obj is JsObject && (obj.Prototype is null || ReferenceEquals(obj.Prototype, engine.Intrinsics.Object.PrototypeObject))))
        {
            if (!seen.Add(obj)) Invalid("Data contains a circular value.");
            if (obj is ArrayInstance)
            {
                writer.WriteStartArray();
                var length = (uint)Own(obj, "length").AsNumber();
                if (length > limits.MaxBoundaryBytes) Invalid("Array exceeds the boundary item limit.");
                for (uint index = 0; index < length; index++) Write(writer, Own(obj, index.ToString(CultureInfo.InvariantCulture)), nullify, seen, depth + 1);
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteStartObject();
                foreach (var key in obj.GetOwnPropertyKeys(Types.String))
                {
                    if (!key.IsString()) Invalid("Symbol keys cannot cross the data boundary.");
                    var name = key.AsString();
                    if (name is "constructor" or "prototype" or "__proto__") Invalid("Blocked data property: " + name);
                    var property = Own(obj, key);
                    if (!nullify && property.IsUndefined()) continue;
                    if (Encoding.UTF8.GetByteCount(name) > limits.MaxBoundaryBytes) Invalid("Property name exceeds the boundary byte limit.");
                    writer.WritePropertyName(name);
                    Write(writer, property, nullify, seen, depth + 1);
                }
                writer.WriteEndObject();
            }
            seen.Remove(obj);
        }
        else Invalid("Only plain data may cross the boundary; functions, promises and runtime references are unavailable.");
        if (writer.BytesCommitted + writer.BytesPending > limits.MaxBoundaryBytes) Invalid("Data exceeds the boundary byte limit.");
    }

    internal sealed class Resolver : IReferenceResolver
    {
        internal JintCodeModeRealm? Realm;
        public bool TryPropertyReference(Engine engine, Reference reference, ref JsValue value)
        {
            if (Realm is null) return false;
            value = Realm.Read(reference.Base, reference.ReferencedName);
            return true;
        }
        public bool TryUnresolvableReference(Engine engine, Reference reference, out JsValue value) { value = JsValue.Undefined; return false; }
        public bool TryGetCallable(Engine engine, object callee, out JsValue value) { value = JsValue.Undefined; return false; }
        public bool CheckCoercible(JsValue value) => !value.IsNull() && !value.IsUndefined();
    }
}
