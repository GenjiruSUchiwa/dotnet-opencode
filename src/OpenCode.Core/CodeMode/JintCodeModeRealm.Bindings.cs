namespace OpenCode.Core.CodeMode;

using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private void InstallBindings()
    {
        engine.Global.DefineOwnProperty("__oc_argumentsTail", new PropertyDescriptor(new ClrFunction(engine, "argumentsTail", (_, args) => Guest(() =>
        {
            var source = (ObjectInstance)args[0];
            var values = new List<JsValue>();
            var length = (uint)Own(source, "length").AsNumber();
            for (var index = (uint)args[1].AsNumber(); index < length; index++)
            {
                checkpoint();
                values.Add(Own(source, index.ToString(CultureInfo.InvariantCulture)));
            }
            return new JsArray(engine, values.ToArray());
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_caught", new PropertyDescriptor(new ClrFunction(engine, "caught", (_, args) => Guest(() =>
            errors.Caught(args[0]))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_forInKeys", new PropertyDescriptor(new ClrFunction(engine, "forInKeys", (_, args) => Guest(() =>
        {
            if (_tools.TryGetValue(args[0], out var path))
                return new JsArray(engine, bindings.Keys(path).Select(key => (JsValue)new JsString(key)).ToArray());
            if (args[0] is not ObjectInstance source || !PlainWritable(source))
                return Unsupported("for...in requires a plain object, array, or tools reference.");
            var keys = new List<JsValue>();
            foreach (var key in source.GetOwnPropertyKeys(Types.String))
            {
                checkpoint();
                if (source.GetOwnProperty(key).Enumerable) keys.Add(key);
            }
            // Source snapshots its key list before the first iteration. A later
            // deletion must not silently remove a key from the remaining traversal.
            return new JsArray(engine, keys.ToArray());
        })), PropertyFlag.AllForbidden));
    }
}
