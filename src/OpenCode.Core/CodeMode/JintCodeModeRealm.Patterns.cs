namespace OpenCode.Core.CodeMode;

using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private void InstallPatterns()
    {
        engine.Global.DefineOwnProperty("__oc_patternAssign", new PropertyDescriptor(new ClrFunction(engine, "patternAssign", (_, args) => Guest(() =>
        {
            AssignPattern(args[1], args[0]);
            return args[0]; // The assignment expression returns the original RHS identity.
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_deleteChain", new PropertyDescriptor(new ClrFunction(engine, "deleteChain", (_, args) => Guest(() =>
            DeleteChain(args[0], args[1].AsBoolean()))), PropertyFlag.AllForbidden));
    }

    private void AssignPattern(JsValue plan, JsValue value)
    {
        checkpoint();
        var kind = Own((ObjectInstance)plan, "kind").AsString();
        if (kind == "target")
        {
            engine.Call(Own((ObjectInstance)plan, "set"), JsValue.Undefined, new[] { value });
            return;
        }
        if (kind == "default")
        {
            var resolved = value.IsUndefined() ? engine.Call(Own((ObjectInstance)plan, "value"), JsValue.Undefined, Array.Empty<JsValue>()) : value;
            AssignPattern(Own((ObjectInstance)plan, "node"), resolved);
            return;
        }
        var entries = (ObjectInstance)Own((ObjectInstance)plan, "entries");
        var count = (uint)Own(entries, "length").AsNumber();
        if (kind == "object")
        {
            if (value is not ObjectInstance source || !PlainWritable(source))
                throw new CodeModeDiagnosticException(new("InvalidDataValue", "Object destructuring requires a data object or array."));
            var consumed = new HashSet<JsValue>();
            for (uint index = 0; index < count; index++)
            {
                checkpoint();
                var entry = (ObjectInstance)Own(entries, index.ToString(CultureInfo.InvariantCulture));
                if (Own(entry, "kind").AsString() == "rest")
                {
                    var rest = new JsObject(engine) { Prototype = null };
                    foreach (var key in source.GetOwnPropertyKeys())
                    {
                        checkpoint();
                        if (consumed.Contains(key) || key.IsString() && key.AsString() is "constructor" or "prototype" or "__proto__") continue;
                        if (!key.IsString() && !IteratorKey(key)) continue;
                        var descriptor = source.GetOwnProperty(key);
                        if (!descriptor.Enumerable) continue;
                        // Copy the stored iterator factory, not a CLR wrapper or
                        // its unwrapped function. Explicit reads retain identity.
                        rest.DefineOwnProperty(key, new PropertyDescriptor(Own(source, key), PropertyFlag.ConfigurableEnumerableWritable));
                    }
                    AssignPattern(Own(entry, "node"), rest);
                    continue;
                }
                var property = MemberKey(engine.Call(Own(entry, "key"), JsValue.Undefined, Array.Empty<JsValue>()));
                consumed.Add(property);
                var item = Read(source, property); // Value/default first; LHS target is resolved by its setter later.
                AssignPattern(Own(entry, "node"), item);
            }
            return;
        }
        if (kind != "array") throw new CodeModeDiagnosticException(new("UnsupportedSyntax", "Unsupported assignment pattern."));
        var cursor = OpenCursor(value);
        var done = false;
        for (uint index = 0; index < count; index++)
        {
            checkpoint();
            var entry = Own(entries, index.ToString(CultureInfo.InvariantCulture));
            var step = done ? (Done: true, Value: JsValue.Undefined) : cursor.Next();
            done = step.Done;
            if (entry.IsNull()) continue; // An elision consumes a step but no binding.
            if (Own((ObjectInstance)entry, "kind").AsString() == "rest")
            {
                var remaining = new List<JsValue>();
                if (!done) remaining.Add(step.Value);
                while (!done)
                {
                    step = cursor.Next();
                    done = step.Done;
                    if (!done)
                    {
                        if (remaining.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Destructuring rest exceeds the item ceiling.");
                        remaining.Add(step.Value);
                    }
                }
                AssignPattern(Own((ObjectInstance)entry, "node"), new JsArray(engine, remaining.ToArray()));
                return;
            }
            if (done) AssignPattern(entry, step.Value);
            else PreserveConsumerError(cursor, () => AssignPattern(entry, step.Value));
        }
        if (!done) cursor.Close(); // A normal early close may report its own failure.
    }

    private JsValue DeleteData(JsValue target, JsValue property)
    {
        var key = MemberKey(property);
        if (IsRegex(target) && key.IsString() && key.AsString() == "lastIndex") return ((ObjectInstance)target).Delete(key) ? JsBoolean.True : JsBoolean.False;
        if (!PlainWritable(target)) return Unsupported("Only data fields may be deleted.");
        return ((ObjectInstance)target).Delete(key) ? JsBoolean.True : JsBoolean.False;
    }

    private JsValue DeleteChain(JsValue value, bool optional)
    {
        checkpoint();
        if (optional && (value.IsNull() || value.IsUndefined())) return JsValue.Null;
        var node = new JsObject(engine) { Prototype = null };
        node.Set("get", new ClrFunction(engine, "get", (_, args) => Guest(() => DeleteChain(Read(value, args[0]), args[1].AsBoolean()))), false);
        node.Set("call", new ClrFunction(engine, "call", (_, args) => Guest(() =>
        {
            var supplied = (ObjectInstance)args[0];
            var length = (uint)Own(supplied, "length").AsNumber();
            var values = new JsValue[length];
            for (uint index = 0; index < length; index++) { checkpoint(); values[index] = Own(supplied, index.ToString(CultureInfo.InvariantCulture)); }
            // Member reads already bind intrinsic receivers. CodeMode functions
            // use no dynamic this; the captured callee is invoked exactly once.
            return DeleteChain(engine.Call(value, JsValue.Undefined, values), args[1].AsBoolean());
        })), false);
        node.Set("remove", new ClrFunction(engine, "remove", (_, args) => Guest(() => DeleteData(value, args[0]))), false);
        return node;
    }
}
