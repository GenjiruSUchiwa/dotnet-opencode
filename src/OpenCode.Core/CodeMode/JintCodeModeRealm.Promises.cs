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
    private readonly Dictionary<string, JsValue> _nativePromiseMethods = new(StringComparer.Ordinal);
    private JsValue _nativeFinally = JsValue.Undefined;

    private void InstallPromiseBoundary()
    {
        foreach (var name in Names("all allSettled race any resolve reject")) _nativePromiseMethods[name] = _promiseConstructor.Get(name);
        _nativeFinally = _promiseConstructor.Get("prototype").Get("finally");
        var facade = new ClrFunction(engine, "Promise", (_, _) => Guest(() => Unsupported("Promise requires new.")));
        facade.DefineOwnProperty("prototype", new PropertyDescriptor(_promiseConstructor.Get("prototype"), PropertyFlag.AllForbidden));
        foreach (var name in Names("all allSettled race any resolve reject"))
        {
            var method = new ClrFunction(engine, name, (_, args) => Guest(() => PromiseStatic(name, args.ToArray())));
            facade.DefineOwnProperty(name, new PropertyDescriptor(method, PropertyFlag.AllForbidden));
            _jsonRejectedCallbacks.Add(method);
        }
        engine.SetValue("Promise", facade);
        _statics[facade] = Names("all allSettled race any resolve reject");
        engine.Global.DefineOwnProperty("__oc_observe", new PropertyDescriptor(new ClrFunction(engine, "observe", (_, args) => promises.Observe(args[0])), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_asyncCall", new PropertyDescriptor(new ClrFunction(engine, "asyncCall", (_, args) =>
        {
            var ordinal = promises.Reserve();
            try { return promises.Track(engine.Call(args[0], JsValue.Undefined, Array.Empty<JsValue>()), ordinal); }
            catch (JavaScriptException error)
            { return promises.Track(engine.Call(_nativePromiseMethods["reject"], _promiseConstructor, new[] { error.Error }), ordinal); }
        }), PropertyFlag.AllForbidden));
    }

    private JsValue ConstructPromise(JsValue executor)
    {
        if (executor is not ScriptFunction) return Unsupported("new Promise(...) expects an executor function.");
        var ordinal = promises.Reserve();
        var wrapped = new ClrFunction(engine, "executor", (_, args) =>
        {
            var reject = args[1];
            var rejection = new ClrFunction(engine, "reject", (_, values) => engine.Call(reject, JsValue.Undefined,
                new[] { errors.Thrown(values.Length == 0 ? JsValue.Undefined : values[0]) }));
            engine.Call(executor, JsValue.Undefined, new JsValue[] { args[0], rejection });
            return JsValue.Undefined;
        });
        return promises.Track(engine.Construct(_promiseConstructor, new JsValue[] { wrapped }), ordinal);
    }

    private JsValue PromiseStatic(string name, JsValue[] args)
    {
        var ordinal = promises.Reserve();
        var input = args.Length == 0 ? JsValue.Undefined : args[0];
        if (name == "resolve") return promises.Track(engine.Call(_nativePromiseMethods[name], _promiseConstructor, new[] { input }), ordinal);
        if (name == "reject") return promises.Track(engine.Call(_nativePromiseMethods[name], _promiseConstructor, new[] { errors.Thrown(input) }), ordinal);
        JsValue pending;
        try
        {
            var items = new List<JsValue>();
            ConsumeIterable(input, item =>
            {
                if (items.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Promise iterable exceeds the item ceiling.");
                promises.Observe(item);
                items.Add(item);
            });
            pending = name == "race" && items.Count == 0
                ? engine.Call(_nativePromiseMethods["reject"], _promiseConstructor, new[] { errors.Runtime(new("ExecutionFailure", "Promise.race([]) would never settle; provide at least one promise or value.")) })
                : engine.Call(_nativePromiseMethods[name], _promiseConstructor, new JsValue[] { new JsArray(engine, items.ToArray()) });
        }
        catch (JavaScriptException error) { pending = engine.Call(_nativePromiseMethods["reject"], _promiseConstructor, new[] { error.Error }); }
        catch (CodeModeDiagnosticException error)
        { pending = engine.Call(_nativePromiseMethods["reject"], _promiseConstructor, new[] { errors.Runtime(error.Diagnostic) }); }
        if (name == "allSettled")
            pending = Then(pending, value =>
            {
                var array = (ObjectInstance)value;
                var length = (uint)Own(array, "length").AsNumber();
                var items = new JsValue[length];
                for (uint index = 0; index < length; index++)
                {
                    checkpoint();
                    var item = (ObjectInstance)Own(array, index.ToString(CultureInfo.InvariantCulture));
                    items[index] = Own(item, "status").AsString() == "rejected"
                        ? JsObject.CreateFromEntries(engine, new Dictionary<string, JsValue> { ["status"] = new JsString("rejected"), ["reason"] = errors.Caught(Own(item, "reason")) })
                        : item;
                }
                return new JsArray(engine, items);
            });
        if (name == "any")
            pending = Then(pending, value => value, error =>
            {
                if (errors.NativeBrand(error) != "AggregateError") throw new JavaScriptException(error);
                var reasons = errors.NormalizeReasons(Own((ObjectInstance)error, "errors"));
                throw new JavaScriptException(errors.Thrown(errors.Create("AggregateError", "All promises were rejected", reasons)));
            });
        return promises.Track(pending, ordinal);
    }

    private JsValue ReadPromise(JsValue parent, string name)
    {
        if (!PromiseMethods.Contains(name)) return Guest(() => throw new CodeModeDiagnosticException(new("InvalidDataValue",
            "This value is an un-awaited Promise; await it first before reading its properties.")));
        var method = new ClrFunction(engine, name, (_, supplied) => Guest(() =>
        {
            promises.Observe(parent);
            var args = supplied.ToArray();
            JsValue Handler(JsValue value, bool rejected)
            {
                if (value is not Function) return JsValue.Undefined;
                RequireCollectionCallback(value, "Promise." + name);
                return new ClrFunction(engine, "reaction", (_, values) => engine.Call(value, JsValue.Undefined,
                    name == "finally" ? Array.Empty<JsValue>() : new[] { rejected ? errors.Caught(values[0]) : values[0] }));
            }
            var first = args.Length == 0 ? JsValue.Undefined : args[0];
            var ordinal = promises.Reserve();
            if (name == "finally") return promises.Track(engine.Call(_nativeFinally, parent, new[] { Handler(first, false) }), ordinal);
            var success = name == "then" ? Handler(first, false) : JsValue.Undefined;
            var failure = Handler(name == "catch" ? first : args.Length > 1 ? args[1] : JsValue.Undefined, true);
            return promises.Track(engine.Call(_promiseThen, parent, new[] { success, failure }), ordinal);
        }));
        _jsonRejectedCallbacks.Add(method);
        return method;
    }
}
