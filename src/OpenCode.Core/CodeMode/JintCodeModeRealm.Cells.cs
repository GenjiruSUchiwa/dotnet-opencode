namespace OpenCode.Core.CodeMode;

using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private sealed class CellEnvironment(Engine engine) : ObjectInstance(engine)
    {
        internal readonly Dictionary<string, JsValue> Values = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Reserved = new(StringComparer.Ordinal);
    }

    private void InstallCells()
    {
        engine.Global.DefineOwnProperty("__oc_cells", new PropertyDescriptor(new ClrFunction(engine, "cells", (_, args) => Guest(() =>
        {
            var environment = new CellEnvironment(engine) { Prototype = null };
            var reserved = (ObjectInstance)args[0];
            var count = (uint)Own(reserved, "length").AsNumber();
            for (uint index = 0; index < count; index++) environment.Reserved.Add(Own(reserved, index.ToString(CultureInfo.InvariantCulture)).AsString());
            return environment;
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_declareCell", new PropertyDescriptor(new ClrFunction(engine, "declareCell", (_, args) => Guest(() =>
        {
            var environment = (CellEnvironment)args[0];
            var name = args[1].AsString();
            if (environment.Reserved.Contains(name) || environment.Values.ContainsKey(name))
                throw new JavaScriptException(Error(engine, new("ExecutionFailure", $"Identifier '{name}' has already been declared.")));
            environment.Values.Add(name, args[2]);
            return args[2];
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_readCell", new PropertyDescriptor(new ClrFunction(engine, "readCell", (_, args) => Guest(() =>
            ReadCell(args[0], args[1].AsString(), args[2], args[3].AsBoolean(), false))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_typeCell", new PropertyDescriptor(new ClrFunction(engine, "typeCell", (_, args) => Guest(() =>
            ReadCell(args[0], args[1].AsString(), args[2], args[3].AsBoolean(), true))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_writeCell", new PropertyDescriptor(new ClrFunction(engine, "writeCell", (_, args) => Guest(() =>
            WriteCell(args[0], args[1].AsString(), args[2], args[3], args[4].AsBoolean()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_compoundCell", new PropertyDescriptor(new ClrFunction(engine, "compoundCell", (_, args) => Guest(() =>
        {
            var chain = args[0]; var name = args[1].AsString(); var getter = args[2]; var setter = args[3]; var missing = args[4].AsBoolean(); var operation = args[5].AsString();
            var current = ReadCell(chain, name, getter, missing, false);
            return new ClrFunction(engine, "commitCell", (_, incoming) => Guest(() => WriteCell(chain, name,
                Compound(operation, current, incoming[0]), setter, missing)));
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_logicalCell", new PropertyDescriptor(new ClrFunction(engine, "logicalCell", (_, args) => Guest(() =>
            new CellSlot(engine, this, args[0], args[1].AsString(), args[3], args[4].AsBoolean(),
                ReadCell(args[0], args[1].AsString(), args[2], args[4].AsBoolean(), false)))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_updateCell", new PropertyDescriptor(new ClrFunction(engine, "updateCell", (_, args) => Guest(() =>
        {
            var current = ReadCell(args[0], args[1].AsString(), args[2], args[4].AsBoolean(), false);
            RequireOperand(current);
            var previous = ValueNumber(current);
            var next = new JsNumber(previous + args[5].AsNumber());
            WriteCell(args[0], args[1].AsString(), next, args[3], args[4].AsBoolean());
            return args[6].AsBoolean() ? next : new JsNumber(previous);
        })), PropertyFlag.AllForbidden));
    }

    private CellEnvironment? FindCell(JsValue chain, string name)
    {
        var list = (ObjectInstance)chain;
        var count = (uint)Own(list, "length").AsNumber();
        for (uint index = 0; index < count; index++)
        {
            checkpoint();
            var environment = (CellEnvironment)Own(list, index.ToString(CultureInfo.InvariantCulture));
            if (environment.Values.ContainsKey(name)) return environment;
        }
        return null;
    }

    private JsValue ReadCell(JsValue chain, string name, JsValue fallback, bool missing, bool type)
    {
        if (FindCell(chain, name) is { } environment)
        {
            var value = environment.Values[name];
            if (!type) return value;
            return new JsString(value.IsUndefined() ? "undefined" : value.IsNull() ? "object" : value.IsString() ? "string" :
                value.IsNumber() ? "number" : value.IsBoolean() ? "boolean" : value.IsSymbol() ? "symbol" : value is Function ? "function" : "object");
        }
        if (missing)
        {
            if (type) return new JsString("undefined");
            throw UnknownCell(name);
        }
        // Native lexical fallback preserves TDZ, including for typeof.
        return engine.Call(fallback, JsValue.Undefined, Array.Empty<JsValue>());
    }

    private JsValue WriteCell(JsValue chain, string name, JsValue value, JsValue fallback, bool missing)
    {
        if (FindCell(chain, name) is { } environment) { environment.Values[name] = value; return value; }
        if (missing) throw UnknownCell(name);
        return engine.Call(fallback, JsValue.Undefined, new[] { value });
    }

    private JavaScriptException UnknownCell(string name) => new(Error(engine,
        new("ExecutionFailure", $"Unknown identifier '{name}'."), "ReferenceError"));

    private sealed class CellSlot : ObjectInstance
    {
        private readonly JintCodeModeRealm _realm;
        private readonly JsValue _chain;
        private readonly string _name;
        private readonly JsValue _setter;
        private readonly bool _missing;
        private readonly JsValue _current;
        internal CellSlot(Engine engine, JintCodeModeRealm realm, JsValue chain, string name, JsValue setter, bool missing, JsValue current) : base(engine)
        { _realm = realm; _chain = chain; _name = name; _setter = setter; _missing = missing; _current = current; Prototype = null; }
        public override JsValue Get(JsValue key, JsValue receiver) => key.IsString() && key.AsString() == "value" ? _current : JsValue.Undefined;
        public override PropertyDescriptor GetOwnProperty(JsValue key) => key.IsString() && key.AsString() == "value"
            ? new PropertyDescriptor(_current, PropertyFlag.Writable) : PropertyDescriptor.Undefined;
        public override bool Set(JsValue key, JsValue value, JsValue receiver)
        {
            if (!key.IsString() || key.AsString() != "value") return false;
            _realm.Guest(() => _realm.WriteCell(_chain, _name, value, _setter, _missing));
            return true;
        }
    }
}
