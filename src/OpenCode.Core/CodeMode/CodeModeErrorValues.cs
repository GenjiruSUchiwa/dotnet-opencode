namespace OpenCode.Core.CodeMode;

using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

/// <summary>Per-engine source error brands and diagnostics; never CLR exception projection.</summary>
internal sealed class CodeModeErrorValues(Engine engine)
{
    private sealed record Info(string Brand, CodeModeDiagnostic? Diagnostic);
    private readonly ConditionalWeakTable<JsValue, Info> _info = new();
    private readonly Dictionary<JsValue, string> _native = new(ReferenceEqualityComparer.Instance);
    private readonly JsValue _toPrimitive = engine.GetValue("Symbol").Get("toPrimitive");

    internal sealed class ThrownValue(Engine engine, JsValue value) : ObjectInstance(engine)
    {
        internal readonly JsValue Value = value;
    }

    internal void RegisterNative(JsValue prototype, string name) => _native[prototype] = name;
    internal string? Brand(JsValue value) => _info.TryGetValue(value, out var info) ? info.Brand : null;
    internal string? NativeBrand(JsValue value) => value is ObjectInstance obj && obj.Prototype is { } prototype ? _native.GetValueOrDefault(prototype) : null;
    internal JsValue Thrown(JsValue value) => new ThrownValue(engine, value) { Prototype = null };
    internal JsValue ThrowPayload(JsValue value) => value is ThrownValue thrown ? thrown.Value : value;

    internal JsValue Create(string name, string message, JsValue? errors = null, CodeModeDiagnostic? diagnostic = null)
    {
        var value = new JsObject(engine) { Prototype = null };
        value.Set("name", new JsString(name), false);
        value.Set("message", new JsString(message), false);
        if (errors is not null) value.Set("errors", errors, false);
        _info.Add(value, new(name, diagnostic));
        // This private hook preserves source coercion without exposing an Error
        // prototype or a public toString method. It is omitted by data copying.
        value.DefineOwnProperty(_toPrimitive, new PropertyDescriptor(new ClrFunction(engine, "errorPrimitive", (_, args) =>
        {
            if (args.Length > 0 && args[0].IsString() && args[0].AsString() == "number") return new JsNumber(double.NaN);
            var currentName = Own(value, "name");
            var currentMessage = Own(value, "message");
            var title = currentName.IsString() ? currentName.AsString() : "Error";
            var text = currentMessage.IsString() ? currentMessage.AsString() : "";
            return new JsString(title.Length == 0 ? text : text.Length == 0 ? title : title + ": " + text);
        }), PropertyFlag.AllForbidden));
        return value;
    }

    internal JsValue Runtime(CodeModeDiagnostic diagnostic, string name = "Error") => Create(name, diagnostic.Message, diagnostic: diagnostic);

    internal JsValue Caught(JsValue value)
    {
        if (value is ThrownValue thrown) return thrown.Value;
        if (_info.TryGetValue(value, out var info))
            return info.Diagnostic is null ? value : Create(info.Brand, info.Diagnostic.Message);
        var name = NativeBrand(value);
        if (name is null) return value;
        var error = (ObjectInstance)value;
        var message = Own(error, "message");
        var errors = Own(error, "errors");
        return Create(name, message.IsString() ? message.AsString() : "", errors.IsUndefined() ? null : NormalizeReasons(errors));
    }

    internal JsValue NormalizeReasons(JsValue value)
    {
        if (value is not ArrayInstance array) return value;
        var length = (uint)Own(array, "length").AsNumber();
        var values = new JsValue[length];
        for (uint index = 0; index < length; index++) values[index] = Caught(Own(array, index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new JsArray(engine, values);
    }

    internal CodeModeDiagnostic Normalize(JsValue value, Func<JsValue, string> programMessage)
    {
        if (value is ThrownValue thrown) return new("ExecutionFailure", "Uncaught: " + programMessage(thrown.Value));
        if (_info.TryGetValue(value, out var info) && info.Diagnostic is not null) return info.Diagnostic;
        if (NativeBrand(value) is { } native)
        {
            var message = Own((ObjectInstance)value, "message");
            return new(native == "SyntaxError" ? "ParseError" : "ExecutionFailure", message.IsString() ? message.AsString() : native);
        }
        return new("ExecutionFailure", "Uncaught: " + programMessage(value));
    }

    private static JsValue Own(ObjectInstance value, string key)
    {
        var descriptor = value.GetOwnProperty(key);
        return descriptor != PropertyDescriptor.Undefined && descriptor.IsDataDescriptor() ? descriptor.Value ?? JsValue.Undefined : JsValue.Undefined;
    }
}
