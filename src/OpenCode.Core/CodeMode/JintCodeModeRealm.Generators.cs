namespace OpenCode.Core.CodeMode;

using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private readonly List<GeneratorValue> _generators = [];
    private sealed record GeneratorRequest(string Kind, JsValue Value, Action<JsValue> Resolve, Action<JsValue> Reject);

    private sealed class GeneratorValue(Engine engine, JsValue factory, bool asynchronous) : ObjectInstance(engine)
    {
        internal JsValue? Factory = factory;
        internal ObjectInstance? Native;
        internal readonly bool Asynchronous = asynchronous;
        internal readonly Queue<GeneratorRequest> Pending = new();
        internal bool Started;
        internal bool Completed;
        internal bool Busy;
        internal bool Settling;
        internal bool Scheduled;
        internal bool Closed;
    }

    private sealed class YieldBox(Engine engine, JsValue value) : ObjectInstance(engine)
    {
        internal readonly JsValue Value = value;
    }

    private void InstallGenerators()
    {
        engine.Global.DefineOwnProperty("__oc_generator", new PropertyDescriptor(new ClrFunction(engine, "generator", (_, args) => Guest(() =>
        {
            var value = new GeneratorValue(engine, args[0], args[1].AsBoolean()) { Prototype = null };
            foreach (var kind in new[] { "next", "return", "throw" })
                value.DefineOwnProperty(kind, new PropertyDescriptor(GeneratorMethod(value, kind), PropertyFlag.AllForbidden));
            value.DefineOwnProperty(value.Asynchronous ? _asyncIteratorSymbol : _iteratorSymbol,
                new PropertyDescriptor(new ClrFunction(engine, "iterator", (_, _) => value), PropertyFlag.AllForbidden));
            _generators.Add(value);
            return value;
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_delegate", new PropertyDescriptor(new ClrFunction(engine, "delegate", (_, args) => Guest(() =>
            CreateDelegation(args[0], args[1].AsBoolean()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_unboxYield", new PropertyDescriptor(new ClrFunction(engine, "unboxYield", (_, args) =>
            UnboxYield(args[0])), PropertyFlag.AllForbidden));
    }

    internal void CloseGenerators()
    {
        // Program completion interrupts unused work; it is NOT a generator return
        // request. Never execute user finally blocks or wait for pending generators.
        foreach (var value in _generators)
        {
            value.Closed = true;
            value.Pending.Clear();
            value.Native = null;
            value.Factory = null;
        }
        _generators.Clear();
    }

    private JsValue GeneratorMethod(GeneratorValue value, string kind) => new ClrFunction(engine, kind, (_, args) => Guest(() =>
        RequestGenerator(value, kind, args.Length == 0 ? JsValue.Undefined : args[0])));

    private JsValue ReadGenerator(GeneratorValue value, string key) => key is "next" or "return" or "throw"
        ? GeneratorMethod(value, key) : JsValue.Undefined;

    private JsValue UnboxYield(JsValue value) => value is YieldBox boxed ? boxed.Value : value;
    private JsValue GeneratorStep(JsValue value, bool done) => JsObject.CreateFromEntries(engine,
        new Dictionary<string, JsValue> { ["value"] = value, ["done"] = done ? JsBoolean.True : JsBoolean.False });

    private JsValue RequestGenerator(GeneratorValue generator, string kind, JsValue input)
    {
        checkpoint();
        if (generator.Closed) throw new OperationCanceledException("The program's generator scope has ended.");
        if (!generator.Asynchronous) return RunSyncGenerator(generator, kind, input);
        if (generator.Pending.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Generator request queue exceeds the item ceiling.");
        var ordinal = promises.Reserve();
        var (promise, resolve, reject) = engine.Advanced.RegisterPromise();
        promises.Track(promise, ordinal);
        generator.Pending.Enqueue(new(kind, input, resolve, reject));
        if (!generator.Busy && !generator.Settling && !generator.Scheduled) BeginGeneratorRequest(generator);
        return promise;
    }

    private ObjectInstance InitializeGenerator(GeneratorValue generator)
    {
        generator.Started = true;
        // The thunk invokes a structurally generated native generator function.
        // Original parameter bindings/defaults are evaluated here, not at handle creation.
        var value = engine.Call(generator.Factory!, JsValue.Undefined, Array.Empty<JsValue>());
        generator.Factory = null;
        if (value is not ObjectInstance native) throw new InvalidOperationException("Generator factory did not return a guest continuation.");
        return generator.Native = native;
    }

    private JsValue RunSyncGenerator(GeneratorValue generator, string kind, JsValue input)
    {
        if (generator.Busy) throw new JavaScriptException(Error(engine, new("ExecutionFailure", "Generator is already running."), "TypeError"));
        if (generator.Completed || !generator.Started && kind != "next")
        {
            generator.Completed = true;
            generator.Factory = null;
            if (kind == "throw") throw new JavaScriptException(errors.Thrown(input));
            return GeneratorStep(kind == "return" ? input : JsValue.Undefined, true);
        }
        generator.Busy = true;
        try
        {
            var native = generator.Native ?? InitializeGenerator(generator);
            var step = RequireIteratorObject(engine.Call(native.Get(kind), native, new[] { kind == "throw" ? errors.Thrown(input) : input }));
            generator.Completed = Truthy(Read(step, "done"));
            return GeneratorStep(UnboxYield(Read(step, "value")), generator.Completed);
        }
        catch { generator.Completed = true; throw; }
        finally { generator.Busy = false; }
    }

    private void BeginGeneratorRequest(GeneratorValue generator)
    {
        checkpoint();
        if (generator.Closed || generator.Busy || generator.Pending.Count == 0) return;
        generator.Busy = true;
        var request = generator.Pending.Dequeue();
        try
        {
            if (generator.Completed || !generator.Started && request.Kind != "next")
            {
                generator.Started = true;
                generator.Completed = true;
                generator.Factory = null;
                if (request.Kind == "throw") { FinishGeneratorRequest(generator, request, errors.Thrown(request.Value), false); return; }
                SettleGeneratorValue(generator, request, request.Kind == "return" ? request.Value : JsValue.Undefined);
                return;
            }
            var native = generator.Native ?? InitializeGenerator(generator);
            var pending = engine.Call(native.Get(request.Kind), native, new[] { request.Kind == "throw" ? errors.Thrown(request.Value) : request.Value });
            Then(pending, step =>
            {
                if (generator.Closed) return JsValue.Undefined;
                var record = RequireIteratorObject(step);
                var done = Truthy(Read(record, "done"));
                var value = UnboxYield(Read(record, "value"));
                if (done)
                {
                    generator.Completed = true;
                    SettleGeneratorValue(generator, request, value);
                }
                else FinishGeneratorRequest(generator, request, GeneratorStep(value, false), true);
                return JsValue.Undefined;
            }, error =>
            {
                generator.Completed = true;
                FinishGeneratorRequest(generator, request, error, false);
                return JsValue.Undefined;
            });
        }
        catch (JavaScriptException error)
        {
            generator.Completed = true;
            FinishGeneratorRequest(generator, request, error.Error, false);
        }
    }

    private void SettleGeneratorValue(GeneratorValue generator, GeneratorRequest request, JsValue value) => Then(value, resolved =>
    {
        FinishGeneratorRequest(generator, request, GeneratorStep(resolved, true), true);
        return JsValue.Undefined;
    }, error =>
    {
        FinishGeneratorRequest(generator, request, error, false);
        return JsValue.Undefined;
    });

    private void FinishGeneratorRequest(GeneratorValue generator, GeneratorRequest request, JsValue result, bool success)
    {
        if (generator.Closed) return;
        generator.Busy = false;
        generator.Settling = true;
        try
        {
            if (success) request.Resolve(result);
            else request.Reject(result);
        }
        finally { generator.Settling = false; }
        if (generator.Pending.Count == 0 || generator.Scheduled || generator.Closed) return;
        generator.Scheduled = true;
        // Continue on the existing guest job queue, not recursive CLR calls or
        // new threads. A completed return(value) waits for value before the next request.
        Then(JsValue.Undefined, _ =>
        {
            generator.Scheduled = false;
            BeginGeneratorRequest(generator);
            return JsValue.Undefined;
        });
    }

    private JsValue Then(JsValue value, Func<JsValue, JsValue> success, Func<JsValue, JsValue>? failure = null)
    {
        var promise = engine.Call(_resolvePromise, _promiseConstructor, new[] { value });
        var fulfilled = new ClrFunction(engine, "fulfilled", (_, args) => Guest(() => success(args[0])));
        return engine.Call(_promiseThen, promise, failure is null ? new JsValue[] { fulfilled } :
            new JsValue[] { fulfilled, new ClrFunction(engine, "rejected", (_, args) => Guest(() => failure(args[0]))) });
    }

    private sealed class Delegation : ObjectInstance
    {
        private readonly JintCodeModeRealm _realm;
        internal readonly ObjectInstance? Original;
        internal readonly JsValue? Next;
        internal readonly SyncCursor? Builtin;
        internal readonly bool InnerAsync;
        internal readonly bool OuterAsync;

        internal Delegation(Engine engine, JintCodeModeRealm realm, ObjectInstance? original, JsValue? next,
            SyncCursor? builtin, bool innerAsync, bool outerAsync) : base(engine)
        {
            _realm = realm; Original = original; Next = next; Builtin = builtin; InnerAsync = innerAsync; OuterAsync = outerAsync;
            Prototype = null;
        }

        public override JsValue Get(JsValue key, JsValue receiver)
        {
            if (ReferenceEquals(key, OuterAsync ? _realm._asyncIteratorSymbol : _realm._iteratorSymbol))
                return new ClrFunction(Engine, "iterator", (_, _) => this);
            if (!key.IsString() || key.AsString() is not ("next" or "return" or "throw")) return JsValue.Undefined;
            var kind = key.AsString();
            return new ClrFunction(Engine, kind, (_, args) => _realm.Guest(() =>
                _realm.ForwardDelegation(this, kind, args.Length == 0 ? JsValue.Undefined : args[0])));
        }
    }

    private JsValue CreateDelegation(JsValue source, bool asynchronous)
    {
        checkpoint();
        if (source.IsString() || source is ArrayInstance or QueryValue || IsCollection(source))
            return new Delegation(engine, this, null, null, OpenCursor(source), false, asynchronous);
        if (source is GeneratorValue generator)
        {
            if (generator.Asynchronous && !asynchronous) return Unsupported("Synchronous yield* cannot consume an async generator.");
            return new Delegation(engine, this, generator, GeneratorMethod(generator, "next"), null, generator.Asynchronous, asynchronous);
        }
        if (source is not ObjectInstance obj || !PlainWritable(obj)) return Unsupported("yield* requires a compatible iterable value.");
        var asyncMethod = asynchronous ? IteratorReadValue(Own(obj, _asyncIteratorSymbol)) : JsValue.Undefined;
        var innerAsync = !asyncMethod.IsNull() && !asyncMethod.IsUndefined();
        var method = innerAsync ? asyncMethod : IteratorReadValue(Own(obj, _iteratorSymbol));
        var result = engine.Call(RequireIteratorMethod(method, "Iterator method"), JsValue.Undefined, Array.Empty<JsValue>());
        var iterator = result as GeneratorValue ?? RequireIteratorObject(result);
        return new Delegation(engine, this, iterator, RequireIteratorMethod(Read(iterator, "next"), "Iterator next"), null, innerAsync, asynchronous);
    }

    private JsValue ForwardDelegation(Delegation delegation, string kind, JsValue input)
    {
        checkpoint();
        if (kind == "throw") input = errors.ThrowPayload(input);
        if (delegation.Builtin is { } cursor)
        {
            if (kind == "next")
            {
                var step = cursor.Next();
                return DelegateRecord(delegation, step.Done ? JsValue.Undefined : step.Value, step.Done, kind);
            }
            cursor.Close();
            if (kind == "throw") return MissingDelegatedThrow();
            return DelegateRecord(delegation, input, true, kind);
        }
        var method = kind == "next" ? delegation.Next! : Read(delegation.Original!, kind);
        if (method.IsUndefined() || method.IsNull())
        {
            if (kind == "return") return DelegateRecord(delegation, input, true, kind);
            var closed = CloseDelegation(delegation);
            return delegation.OuterAsync ? Then(closed, _ => MissingDelegatedThrow()) : MissingDelegatedThrow();
        }
        var called = engine.Call(RequireIteratorMethod(method, "Iterator " + kind), JsValue.Undefined, new[] { input });
        return delegation.InnerAsync ? Then(called, ReadRecord) : ReadRecord(called);

        JsValue ReadRecord(JsValue value)
        {
            var record = RequireIteratorObject(value);
            var done = Truthy(Read(record, "done"));
            return DelegateRecord(delegation, Read(record, "value"), done, kind);
        }
    }

    private JsValue DelegateRecord(Delegation delegation, JsValue value, bool done, string kind)
    {
        // Boxing prevents native async yield* from implicitly awaiting an async
        // iterator's nested value. Source async-from-sync values DO get awaited.
        JsValue Box(JsValue item) => GeneratorStep(new YieldBox(engine, item) { Prototype = null }, done);
        if (!delegation.OuterAsync || delegation.InnerAsync) return Box(value);
        return Then(value, Box, error =>
        {
            if (kind != "return" && !done)
            {
                try { CloseDelegation(delegation, awaitSyncValue: false); }
                catch (OperationCanceledException) { throw; }
                catch { } // A cleanup failure does not replace the rejected yielded value.
            }
            throw new JavaScriptException(error);
        });
    }

    private JsValue CloseDelegation(Delegation delegation, bool awaitSyncValue = true)
    {
        checkpoint();
        if (delegation.Builtin is { } cursor) { cursor.Close(); return JsValue.Undefined; }
        var method = Read(delegation.Original!, "return");
        if (method.IsUndefined() || method.IsNull()) return JsValue.Undefined;
        var called = engine.Call(RequireIteratorMethod(method, "Iterator return"), JsValue.Undefined, Array.Empty<JsValue>());
        if (delegation.InnerAsync) return Then(called, value => { RequireIteratorObject(value); return JsValue.Undefined; });
        var record = RequireIteratorObject(called);
        return delegation.OuterAsync && awaitSyncValue ? Then(Read(record, "value"), _ => JsValue.Undefined) : JsValue.Undefined;
    }

    private JsValue MissingDelegatedThrow() => throw new JavaScriptException(Error(engine,
        new("ExecutionFailure", "The delegated iterator does not provide a throw() method."), "TypeError"));
}
