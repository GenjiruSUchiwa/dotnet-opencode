namespace OpenCode.Core.CodeMode;

using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private void InstallSyntaxGuards()
    {
        engine.Global.DefineOwnProperty("__oc_unsupported", new PropertyDescriptor(new ClrFunction(engine, "unsupported", (_, args) =>
            Guest(() => Unsupported("Syntax '" + args[0].AsString() + "' is not supported."))), PropertyFlag.AllForbidden));
        foreach (var name in Names("Array Object Promise Error TypeError RangeError SyntaxError ReferenceError EvalError URIError AggregateError"))
        {
            var constructor = engine.GetValue(name);
            engine.Global.DefineOwnProperty("__oc_new" + name, new PropertyDescriptor(new ClrFunction(engine, "new" + name, (_, supplied) => Guest(() =>
            {
                var args = supplied.ToArray();
                if (name == "Object")
                {
                    if (args.Length == 0 || args[0].IsNull() || args[0].IsUndefined()) return new JsObject(engine);
                    if (args[0] is ObjectInstance) return args[0];
                    return Unsupported("Object wrapper primitives are not supported; use the primitive directly.");
                }
                if (name == "Array") return engine.Construct(constructor, args);
                if (name == "Promise") return ConstructPromise(args.Length == 0 ? JsValue.Undefined : args[0]);
                return ConstructError(name, args);
            })), PropertyFlag.AllForbidden));
        }
    }
}
