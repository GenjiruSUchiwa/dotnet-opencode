namespace OpenCode.Core.CodeMode;

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
    private JsValue _asyncIteratorSymbol = JsValue.Undefined;
    private readonly Dictionary<JsValue, JsValue> _iteratorOriginals = new(ReferenceEqualityComparer.Instance);
    private JsValue _resolvePromise = JsValue.Undefined;
    private JsValue _promiseConstructor = JsValue.Undefined;
    private JsValue _promiseThen = JsValue.Undefined;

    private void InstallIterators()
    {
        _promiseConstructor = engine.GetValue("Promise");
        _resolvePromise = _promiseConstructor.Get("resolve");
        _promiseThen = _promiseConstructor.Get("prototype").Get("then");
        var symbols = new ClrFunction(engine, "Symbol", (_, _) => Unsupported("Only Symbol.iterator and Symbol.asyncIterator are available."));
        symbols.DefineOwnProperty("iterator", new PropertyDescriptor(_iteratorSymbol, PropertyFlag.AllForbidden));
        symbols.DefineOwnProperty("asyncIterator", new PropertyDescriptor(_asyncIteratorSymbol, PropertyFlag.AllForbidden));
        engine.SetValue("Symbol", symbols);
        _statics[symbols] = Names("iterator asyncIterator");
        _jsonRejectedCallbacks.Add(symbols);
        engine.Global.DefineOwnProperty("__oc_property", new PropertyDescriptor(new ClrFunction(engine, "property", (_, args) => Guest(() =>
        {
            var key = MemberKey(args[0]);
            return new ClrFunction(engine, "propertyValue", (_, values) => Guest(() =>
            {
                var value = new JsObject(engine) { Prototype = null };
                value.DefineOwnProperty(key, new PropertyDescriptor(IteratorStoredValue(key, values[0]), PropertyFlag.ConfigurableEnumerableWritable));
                return value;
            }));
        })), PropertyFlag.AllForbidden));
    }

    private bool IteratorKey(JsValue key) => ReferenceEquals(key, _iteratorSymbol) || ReferenceEquals(key, _asyncIteratorSymbol);
    private JsValue IteratorReadValue(JsValue stored) => _iteratorOriginals.GetValueOrDefault(stored) ?? stored;

    private JsValue IteratorStoredValue(JsValue key, JsValue value)
    {
        if (!IteratorKey(key)) return value;
        var original = IteratorReadValue(value);
        if (original is not Function) return original;
        var asynchronous = ReferenceEquals(key, _asyncIteratorSymbol);
        var wrapped = new ClrFunction(engine, "iteratorFactory", (_, args) => Guest(() =>
        {
            var value = engine.Call(original, JsValue.Undefined, args);
            return WrapIterator(value as GeneratorValue ?? RequireIteratorObject(value), asynchronous);
        }));
        _iteratorOriginals[wrapped] = original;
        return wrapped;
    }

    private ObjectInstance RequireIteratorObject(JsValue value)
    {
        if (value is ObjectInstance obj && PlainWritable(obj)) return obj;
        throw new CodeModeDiagnosticException(new("InvalidDataValue", "Iterator methods and steps must return data objects, not runtime references."));
    }

    private JsValue RequireIteratorMethod(JsValue value, string name)
    {
        if (value is Function) return value;
        throw new CodeModeDiagnosticException(new("ExecutionFailure", name + " must be a function."));
    }

    private JsValue WrapIterator(ObjectInstance original, bool asynchronous)
    {
        var next = RequireIteratorMethod(Read(original, "next"), "Iterator next");
        var iterator = new JsObject(engine) { Prototype = null };
        iterator.Set("next", new ClrFunction(engine, "next", (_, args) => Guest(() =>
            Settle(engine.Call(next, JsValue.Undefined, args)))), false);
        iterator.Set("return", new ClrFunction(engine, "return", (_, args) => Guest(() =>
        {
            // A checkpoint occurs before user cleanup. Cancellation/time limits
            // cannot be extended by an iterator's return() implementation.
            var method = Read(original, "return");
            if (method.IsUndefined() || method.IsNull()) return Step(JsBoolean.True, JsValue.Undefined);
            return Settle(engine.Call(RequireIteratorMethod(method, "Iterator return"), JsValue.Undefined, args));
        })), false);
        iterator.DefineOwnProperty(asynchronous ? _asyncIteratorSymbol : _iteratorSymbol,
            new PropertyDescriptor(new ClrFunction(engine, "iterator", (_, _) => iterator), PropertyFlag.AllForbidden));
        return iterator;

        JsValue Settle(JsValue value)
        {
            if (!asynchronous) return ValidateStep(value);
            var promise = engine.Call(_resolvePromise, _promiseConstructor, new[] { value });
            return engine.Call(_promiseThen, promise, new JsValue[]
            {
                new ClrFunction(engine, "iteratorStep", (_, result) => Guest(() => ValidateStep(result[0])))
            });
        }
    }

    private JsValue ValidateStep(JsValue value)
    {
        checkpoint();
        var step = RequireIteratorObject(value);
        return Step(Truthy(Read(step, "done")) ? JsBoolean.True : JsBoolean.False, Read(step, "value"));
    }

    private JsValue Step(JsValue done, JsValue value) => JsObject.CreateFromEntries(engine,
        new Dictionary<string, JsValue> { ["done"] = done, ["value"] = value });

    private static bool Truthy(JsValue value) => !value.IsNull() && !value.IsUndefined() &&
        (value.IsBoolean() ? value.AsBoolean() : value.IsString() ? value.AsString().Length > 0 :
            !value.IsNumber() || value.AsNumber() != 0 && !double.IsNaN(value.AsNumber()));

    private bool HasCustomIterator(JsValue source) => source is JsObject obj && PlainWritable(obj) &&
        Own(obj, _iteratorSymbol) is { } method && !method.IsNull() && !method.IsUndefined();

    private sealed record SyncCursor(Func<(bool Done, JsValue Value)> Next, Action Close);

    private SyncCursor OpenCursor(JsValue source)
    {
        checkpoint();
        if (source is GeneratorValue generator)
        {
            if (generator.Asynchronous) throw new CodeModeDiagnosticException(new("ExecutionFailure", "A synchronous iterator cannot consume an async generator."));
            return new(() =>
            {
                var step = (ObjectInstance)RequestGenerator(generator, "next", JsValue.Undefined);
                return (Truthy(Read(step, "done")), Read(step, "value"));
            }, () => { RequestGenerator(generator, "return", JsValue.Undefined); });
        }
        if (!HasCustomIterator(source))
        {
            if (!source.IsString() && source is not (ArrayInstance or QueryValue) && !IsCollection(source))
                throw new CodeModeDiagnosticException(new("ExecutionFailure", "A supported synchronous iterable is required."));
            var sequence = IterateCollection(source).GetEnumerator();
            return new(() => sequence.MoveNext() ? (false, sequence.Current) : (true, JsValue.Undefined), sequence.Dispose);
        }
        var factory = RequireIteratorMethod(Own((ObjectInstance)source, _iteratorSymbol), "Symbol.iterator");
        var iterator = RequireIteratorObject(engine.Call(factory, JsValue.Undefined, Array.Empty<JsValue>()));
        var next = RequireIteratorMethod(Read(iterator, "next"), "Iterator next");
        return new(() =>
        {
            checkpoint();
            var step = RequireIteratorObject(engine.Call(next, JsValue.Undefined, Array.Empty<JsValue>()));
            return (Truthy(Read(step, "done")), Read(step, "value"));
        }, () =>
        {
            checkpoint();
            var method = Read(iterator, "return");
            if (method.IsNull() || method.IsUndefined()) return;
            RequireIteratorObject(engine.Call(RequireIteratorMethod(method, "Iterator return"), JsValue.Undefined, Array.Empty<JsValue>()));
        });
    }

    private void ConsumeIterable(JsValue source, Action<JsValue> consume)
    {
        var cursor = OpenCursor(source);
        while (true)
        {
            var step = cursor.Next(); // Acquisition/next failures do not invoke return().
            if (step.Done) return;
            PreserveConsumerError(cursor, () => consume(step.Value));
        }
    }

    private static void PreserveConsumerError(SyncCursor cursor, Action consume)
    {
        try { consume(); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Source preserveConsumerError: cleanup runs, but never replaces the
            // original consumer/binding failure. A normal early close may throw.
            try { cursor.Close(); }
            catch { }
            throw;
        }
    }
}
