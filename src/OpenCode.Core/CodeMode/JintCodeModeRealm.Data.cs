namespace OpenCode.Core.CodeMode;

using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private void InstallDataViews()
    {
        engine.Global.DefineOwnProperty("__oc_destructure", new PropertyDescriptor(new ClrFunction(engine, "destructure", (_, args) => Guest(() =>
        {
            using var document = JsonDocument.Parse(args[1].AsString(), new JsonDocumentOptions { MaxDepth = 33 });
            return Projection(args[0], document.RootElement.Clone());
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_objectSpread", new PropertyDescriptor(new ClrFunction(engine, "objectSpread", (_, args) => Guest(() =>
        {
            if (args[0].IsNull() || args[0].IsUndefined() || IsBuiltinValue(args[0])) return new JsObject(engine) { Prototype = null };
            if (args[0] is ArrayInstance) return Unsupported("Object spread requires a data object, not an array.");
            return Projection(args[0], null);
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_iterableSpread", new PropertyDescriptor(new ClrFunction(engine, "iterableSpread", (_, args) => Guest(() =>
        {
            // The supported iterable surface has no guest-defined iterator hooks.
            // Native array/string iteration preserves holes and Unicode code points.
            if (args[0].IsString() || args[0] is ArrayInstance or QueryValue or GeneratorValue { Asynchronous: false } || IsCollection(args[0]) || HasCustomIterator(args[0])) return args[0];
            return Unsupported("Array and argument spread require a supported synchronous iterable.");
        })), PropertyFlag.AllForbidden));
    }

    private JsValue Projection(JsValue value, JsonElement? plan)
    {
        checkpoint();
        if (value is not ObjectInstance obj || _tools.ContainsKey(obj) || _statics.ContainsKey(obj) ||
            !(obj is ArrayInstance || obj is JsObject && (obj.Prototype is null || ReferenceEquals(obj.Prototype, engine.Intrinsics.Object.PrototypeObject))))
            return Unsupported("Object destructuring and spread require a plain data object (destructuring also accepts arrays), not a runtime reference.");
        return new DataView(engine, this, obj, plan);
    }

    /// <summary>
    /// A guest object, never an ObjectWrapper. Native destructuring/spread gets an
    /// own-field view rather than the original namespace/prototype. Values are not
    /// JSON-copied, so leaf identity is retained. Nested projections are lazy: an
    /// earlier default initializer still runs before a later nested pattern fails.
    /// </summary>
    private sealed class DataView : ObjectInstance
    {
        private readonly JintCodeModeRealm _realm;
        private readonly ObjectInstance _source;
        private readonly JsonElement? _plan;

        internal DataView(Engine engine, JintCodeModeRealm realm, ObjectInstance source, JsonElement? plan) : base(engine)
        {
            _realm = realm;
            _source = source;
            _plan = plan;
            Prototype = null;
        }

        public override JsValue Get(JsValue property, JsValue receiver) => _realm.Guest(() =>
        {
            RequireKey(property);
            if (_realm.IteratorKey(property)) return Own(_source, property); // Preserve guarded factories during rest/spread copies.
            var value = _realm.Read(_source, property);
            if (value.IsUndefined() || _plan is not { } plan || !plan.TryGetProperty(property.AsString(), out var nested)) return value;
            return _realm.Projection(value, nested);
        });

        public override PropertyDescriptor GetOwnProperty(JsValue property)
        {
            RequireKey(property);
            var descriptor = _source.GetOwnProperty(property);
            if (descriptor == PropertyDescriptor.Undefined) return descriptor;
            if (!descriptor.IsDataDescriptor()) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Accessor properties are unavailable."));
            return new PropertyDescriptor(JsValue.Undefined, descriptor.Enumerable ? PropertyFlag.Enumerable : PropertyFlag.AllForbidden);
        }

        public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
        {
            var keys = _source.GetOwnPropertyKeys(types).Where(key => key.IsString() || _realm.IteratorKey(key)).ToList();
            foreach (var key in keys) RequireKey(key);
            return keys;
        }

        private void RequireKey(JsValue key)
        {
            _realm.CheckDataView();
            if (_realm.IteratorKey(key)) return;
            if (!key.IsString() || key.AsString() is "constructor" or "prototype" or "__proto__")
                throw new CodeModeDiagnosticException(new("InvalidDataValue", "Blocked data property in destructuring or spread."));
        }
    }

    // The nested guest object cannot directly capture the outer primary constructor parameter.
    private void CheckDataView() => checkpoint();
}
