namespace OpenCode.Core.CodeMode;

using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private sealed class PatternScope(Engine engine) : ObjectInstance(engine)
    {
        internal readonly List<PatternArray> Frames = [];
    }

    private sealed class PatternArray(Engine engine, PatternScope scope, SyncCursor cursor) : ObjectInstance(engine)
    {
        internal readonly PatternScope Scope = scope;
        internal readonly SyncCursor Cursor = cursor;
        internal bool Done;
        internal bool Active = true;
    }

    private sealed class PatternObject(Engine engine, ObjectInstance source) : ObjectInstance(engine)
    {
        internal readonly ObjectInstance Source = source;
        internal readonly HashSet<JsValue> Consumed = [];
    }

    private void InstallPatternScopes()
    {
        engine.Global.DefineOwnProperty("__oc_patternScope", new PropertyDescriptor(new ClrFunction(engine, "patternScope", (_, _) =>
            new PatternScope(engine)), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternArray", new PropertyDescriptor(new ClrFunction(engine, "patternArray", (_, args) => Guest(() =>
        {
            var scope = (PatternScope)args[0];
            // Acquisition failure does not add a frame. An enclosing pattern's
            // frame remains active and is closed by the generated catch boundary.
            var frame = new PatternArray(engine, scope, OpenCursor(args[1]));
            scope.Frames.Add(frame);
            return frame;
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternNext", new PropertyDescriptor(new ClrFunction(engine, "patternNext", (_, args) =>
            Guest(() => PatternNext((PatternArray)args[0]))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternTail", new PropertyDescriptor(new ClrFunction(engine, "patternTail", (_, args) => Guest(() =>
        {
            var frame = (PatternArray)args[0];
            var values = new List<JsValue>();
            while (!frame.Done)
            {
                var value = PatternNext(frame);
                if (frame.Done) break;
                if (values.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Destructuring rest exceeds the item ceiling.");
                values.Add(value);
            }
            return new JsArray(engine, values.ToArray());
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternFinish", new PropertyDescriptor(new ClrFunction(engine, "patternFinish", (_, args) => Guest(() =>
        {
            FinishPattern((PatternArray)args[0]);
            return JsValue.Undefined;
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternUnwind", new PropertyDescriptor(new ClrFunction(engine, "patternUnwind", (_, args) =>
        {
            var scope = (PatternScope)args[0];
            while (scope.Frames.Count > 0)
            {
                // preserveConsumerError: retain the original throw. The cursor's
                // own checkpoint prevents user return() after cancellation.
                try { FinishPattern(scope.Frames[^1]); }
                catch { }
            }
            return JsValue.Undefined;
        }), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternObject", new PropertyDescriptor(new ClrFunction(engine, "patternObject", (_, args) => Guest(() =>
        {
            if (args[0] is not ObjectInstance source || !PlainWritable(source))
                throw new CodeModeDiagnosticException(new("InvalidDataValue", "Object destructuring requires a data object or array."));
            return new PatternObject(engine, source);
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternRead", new PropertyDescriptor(new ClrFunction(engine, "patternRead", (_, args) => Guest(() =>
        {
            var frame = (PatternObject)args[0];
            var key = MemberKey(args[1]);
            frame.Consumed.Add(key);
            return Read(frame.Source, key);
        })), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_patternRest", new PropertyDescriptor(new ClrFunction(engine, "patternRest", (_, args) => Guest(() =>
        {
            var frame = (PatternObject)args[0];
            var rest = new JsObject(engine) { Prototype = null };
            foreach (var key in frame.Source.GetOwnPropertyKeys())
            {
                checkpoint();
                if (frame.Consumed.Contains(key) || key.IsString() && key.AsString() is "constructor" or "prototype" or "__proto__") continue;
                if (!key.IsString() && !IteratorKey(key)) continue;
                if (!frame.Source.GetOwnProperty(key).Enumerable) continue;
                rest.DefineOwnProperty(key, new PropertyDescriptor(Own(frame.Source, key), PropertyFlag.ConfigurableEnumerableWritable));
            }
            return rest;
        })), PropertyFlag.AllForbidden));
    }

    private JsValue PatternNext(PatternArray frame)
    {
        if (frame.Done) return JsValue.Undefined;
        try
        {
            var step = frame.Cursor.Next();
            frame.Done = step.Done;
            return step.Done ? JsValue.Undefined : step.Value;
        }
        catch
        {
            // Source next/step-validation failures never call this iterator's
            // return(). Outer consumer frames still unwind in their own order.
            frame.Active = false;
            frame.Scope.Frames.Remove(frame);
            throw;
        }
    }

    private void FinishPattern(PatternArray frame)
    {
        if (!frame.Active) return;
        frame.Active = false;
        frame.Scope.Frames.Remove(frame);
        if (!frame.Done) frame.Cursor.Close();
    }
}
