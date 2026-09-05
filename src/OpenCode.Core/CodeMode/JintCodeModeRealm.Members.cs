namespace OpenCode.Core.CodeMode;

using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private JsValue _numberPower = JsValue.Undefined;
    private static readonly HashSet<string> CompoundOperators = Names("+= -= *= /= %= **= &= |= ^= <<= >>= >>>=");
    private static readonly HashSet<string> SourceArrayMethods = Names("map filter find findIndex findLast findLastIndex some every includes join reduce reduceRight flatMap forEach sort toSorted slice concat indexOf lastIndexOf at flat reverse toReversed with push pop shift unshift splice toSpliced fill copyWithin keys values entries");
    private sealed record DataReference(ObjectInstance Target, JsValue Key, JsValue Current);

    private void InstallMemberWrites()
    {
        _numberPower = engine.GetValue("Math").Get("pow");
        engine.Global.DefineOwnProperty("__oc_assign", new PropertyDescriptor(new ClrFunction(engine, "assign", (_, args) => Guest(() =>
        {
            var reference = Reference(args[0], args[1]);
            var operation = args[2].AsString();
            // The function retains the old value and receiver across an awaited
            // RHS. It is invocation-local, not a global temporary reference slot.
            return new ClrFunction(engine, "commit", (_, values) => Guest(() =>
            {
                var next = operation == "=" ? values[0] : Compound(operation, reference.Current, values[0]);
                WriteReference(reference, next);
                return next;
            }));
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_logicalRef", new PropertyDescriptor(new ClrFunction(engine, "logicalRef", (_, args) => Guest(() =>
            new LogicalSlot(engine, this, Reference(args[0], args[1])))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_updateMember", new PropertyDescriptor(new ClrFunction(engine, "updateMember", (_, args) => Guest(() =>
        {
            var reference = Reference(args[0], args[1]);
            RequireOperand(reference.Current);
            var previous = ValueNumber(reference.Current);
            var next = new JsNumber(previous + args[2].AsNumber());
            WriteReference(reference, next);
            return args[3].AsBoolean() ? next : new JsNumber(previous);
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_updateValue", new PropertyDescriptor(new ClrFunction(engine, "updateValue", (_, args) => Guest(() =>
        {
            RequireOperand(args[0]);
            var previous = ValueNumber(args[0]);
            return JsObject.CreateFromEntries(engine, new Dictionary<string, JsValue>
            {
                ["previous"] = new JsNumber(previous), ["next"] = new JsNumber(previous + args[1].AsNumber())
            });
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_compound", new PropertyDescriptor(new ClrFunction(engine, "compound", (_, args) => Guest(() =>
            Compound(args[0].AsString(), args[1], args[2]))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_delete", new PropertyDescriptor(new ClrFunction(engine, "delete", (_, args) => Guest(() =>
            DeleteData(args[0], args[1]))), PropertyFlag.AllForbidden));
    }

    private JsValue MemberKey(JsValue value)
    {
        checkpoint();
        if (IteratorKey(value)) return value;
        if (!value.IsString() && !value.IsNumber()) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Property key must be a string or number."));
        var key = value.IsString() ? value.AsString() : ValueString(value);
        if (key is "constructor" or "prototype" or "__proto__")
            throw new CodeModeDiagnosticException(new("InvalidDataValue", "Property '" + key + "' is not available."));
        return new JsString(key);
    }

    private bool PlainWritable(JsValue value) => value is ObjectInstance obj && !_tools.ContainsKey(value) && !_statics.ContainsKey(value) &&
        (obj is ArrayInstance || obj is JsObject && (obj.Prototype is null || ReferenceEquals(obj.Prototype, engine.Intrinsics.Object.PrototypeObject)));

    private DataReference Reference(JsValue value, JsValue property)
    {
        var key = MemberKey(property);
        if (IsRegex(value))
        {
            if (!key.IsString() || key.AsString() != "lastIndex") throw new CodeModeDiagnosticException(new("InvalidDataValue", "Only RegExp.lastIndex may be assigned."));
            return new((ObjectInstance)value, key, Own((ObjectInstance)value, key));
        }
        if (value is UrlValue url)
        {
            if (!key.IsString() || !UrlProperties.Contains(key.AsString()) || key.AsString() == "searchParams")
                throw new CodeModeDiagnosticException(new("InvalidDataValue", "Only URL data fields may be assigned."));
            // origin is a reference in the source; read-only rejection occurs at
            // write time, after the RHS, and not for a skipped logical assignment.
            return new(url, key, ReadUrl(url, key.AsString()));
        }
        if (!PlainWritable(value)) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Only data fields may be assigned; runtime references are opaque."));
        var target = (ObjectInstance)value;
        if (target is ArrayInstance)
        {
            if (key.IsString() && key.AsString() == "length") throw new CodeModeDiagnosticException(new("InvalidDataValue", "Array length cannot be assigned."));
            if (key.IsString() && SourceArrayMethods.Contains(key.AsString())) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Array methods cannot be assigned."));
            if (!key.IsString() || Index(key.AsString()) is null) throw new CodeModeDiagnosticException(new("InvalidDataValue", "Array assignment requires a canonical array index."));
        }
        return new(target, key, IteratorKey(key) ? IteratorReadValue(Own(target, key)) : Own(target, key));
    }

    private void WriteReference(DataReference reference, JsValue value)
    {
        checkpoint();
        if (reference.Target is UrlValue)
        {
            engine.Call(UrlSetter(reference.Target, reference.Key.AsString()), JsValue.Undefined, new[] { value });
            return;
        }
        if (IsRegex(reference.Target)) { reference.Target.Set(reference.Key, value, false); return; }
        if (reference.Target is ArrayInstance && Index(reference.Key.AsString()) is { } index && index >= Math.Min(limits.MaxBoundaryBytes, 100000))
            Invalid("Array assignment exceeds the configured item ceiling.");
        RejectCircularInsertion(reference.Target, value);
        // Define an own data property, never an inherited setter. Existing plain
        // data/accessor validation took place while capturing the reference.
        if (!reference.Target.Set(reference.Key, IteratorStoredValue(reference.Key, value), false))
            Invalid("The data field could not be assigned.");
    }

    private void RejectCircularInsertion(ObjectInstance target, JsValue value)
    {
        var pending = new Stack<JsValue>();
        var seen = new HashSet<JsValue>(ReferenceEqualityComparer.Instance);
        pending.Push(value);
        while (pending.TryPop(out var item))
        {
            checkpoint();
            if (ReferenceEquals(item, target)) Invalid("Assignment would create a circular data value.");
            // Closure and builtin-value internals are opaque, as in the source.
            if (item is not ObjectInstance obj || !PlainWritable(obj) || !seen.Add(item)) continue;
            if (obj is ArrayInstance)
            {
                var length = (uint)Own(obj, "length").AsNumber();
                for (uint index = 0; index < length; index++) { checkpoint(); pending.Push(Own(obj, index.ToString(CultureInfo.InvariantCulture))); }
            }
            else
                foreach (var key in JsonKeys(obj)) pending.Push(Own(obj, key));
        }
    }

    private void RequireOperand(JsValue value)
    {
        var pending = new Stack<JsValue>();
        var seen = new HashSet<JsValue>(ReferenceEqualityComparer.Instance);
        pending.Push(value);
        while (pending.TryPop(out var item))
        {
            checkpoint();
            if (IsBuiltinValue(item)) continue;
            if (item is not ObjectInstance obj) continue;
            if (!PlainWritable(obj)) Invalid("Compound and update operators require data values, not opaque runtime references.");
            if (!seen.Add(item)) continue;
            if (obj is ArrayInstance)
            {
                var length = (uint)Own(obj, "length").AsNumber();
                for (uint index = 0; index < length; index++) { checkpoint(); pending.Push(Own(obj, index.ToString(CultureInfo.InvariantCulture))); }
            }
            else foreach (var key in JsonKeys(obj)) pending.Push(Own(obj, key));
        }
    }

    private JsValue Compound(string operation, JsValue left, JsValue right)
    {
        if (!CompoundOperators.Contains(operation)) return Unsupported("Unsupported compound assignment operator: " + operation);
        RequireOperand(left);
        RequireOperand(right);
        JsValue Primitive(JsValue value) => IsDate(value) && operation != "+=" ? DateCall(value, "getTime") :
            value is ObjectInstance ? new JsString(ValueString(value)) : value;
        var lhs = Primitive(left);
        var rhs = Primitive(right);
        if (operation == "+=" && (lhs.IsString() || rhs.IsString())) return new JsString(ValueString(lhs) + ValueString(rhs));
        var a = ValueNumber(lhs);
        var b = ValueNumber(rhs);
        var result = operation switch
        {
            "+=" => a + b, "-=" => a - b, "*=" => a * b, "/=" => a / b, "%=" => a % b,
            "**=" => engine.Call(_numberPower, JsValue.Undefined, new JsValue[] { new JsNumber(a), new JsNumber(b) }).AsNumber(),
            "&=" => unchecked((int)Uint32(a)) & unchecked((int)Uint32(b)),
            "|=" => unchecked((int)Uint32(a)) | unchecked((int)Uint32(b)),
            "^=" => unchecked((int)Uint32(a)) ^ unchecked((int)Uint32(b)),
            "<<=" => unchecked((int)Uint32(a)) << (int)(Uint32(b) & 31),
            ">>=" => unchecked((int)Uint32(a)) >> (int)(Uint32(b) & 31),
            ">>>=" => Uint32(a) >> (int)(Uint32(b) & 31),
            _ => throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Unknown assignment operator."))
        };
        return new JsNumber(result);
    }

    private static uint Uint32(double value)
    {
        if (!double.IsFinite(value) || value == 0) return 0;
        var result = Math.Truncate(value) % 4294967296d;
        return (uint)(result < 0 ? result + 4294967296d : result);
    }

    /// <summary>
    /// Native logical assignment supplies the short-circuit itself. This guest
    /// slot holds one captured reference; its setter commits only when JS writes.
    /// It is not a CLR wrapper and is never returned to the submitted program.
    /// </summary>
    private sealed class LogicalSlot : ObjectInstance
    {
        private readonly JintCodeModeRealm _realm;
        private readonly DataReference _reference;
        internal LogicalSlot(Engine engine, JintCodeModeRealm realm, DataReference reference) : base(engine)
        { _realm = realm; _reference = reference; Prototype = null; }
        public override JsValue Get(JsValue key, JsValue receiver) => key.IsString() && key.AsString() == "value" ? _reference.Current : JsValue.Undefined;
        public override PropertyDescriptor GetOwnProperty(JsValue key) => key.IsString() && key.AsString() == "value"
            ? new PropertyDescriptor(_reference.Current, PropertyFlag.Writable) : PropertyDescriptor.Undefined;
        public override bool Set(JsValue key, JsValue value, JsValue receiver)
        {
            if (!key.IsString() || key.AsString() != "value") return false;
            _realm.Guest(() => { _realm.WriteReference(_reference, value); return value; });
            return true;
        }
    }
}
