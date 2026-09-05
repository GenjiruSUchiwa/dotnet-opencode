namespace OpenCode.Core.CodeMode;

using System.Globalization;
using System.Text;
using System.Text.Json;
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
    private JsValue _nativeJsonStringify = JsValue.Undefined;
    private JsValue _nativeStringConvert = JsValue.Undefined;
    private readonly HashSet<JsValue> _jsonRejectedCallbacks = new(ReferenceEqualityComparer.Instance);

    private void InstallJson()
    {
        // Only primitive quoting/number formatting uses the original serializer.
        // Guest objects never reach its getter/toJSON or replacer paths.
        _nativeJsonStringify = engine.GetValue("JSON").Get("stringify");
        _nativeStringConvert = engine.GetValue("String");
        foreach (var name in Names("all allSettled race any resolve reject"))
            _jsonRejectedCallbacks.Add(engine.GetValue("Promise").Get(name));
        var json = new JsObject(engine);
        json.Set("parse", new ClrFunction(engine, "parse", (_, args) => Guest(() => ParseJson(args.ToArray()))), false);
        json.Set("stringify", new ClrFunction(engine, "stringify", (_, args) => Guest(() => StringifyJson(args.ToArray()))), false);
        engine.SetValue("JSON", json);
        _statics[json] = Names("parse stringify");
    }

    private bool JsonCallback(JsValue value)
    {
        if (value is not Function) return false;
        if (_tools.ContainsKey(value) || _jsonRejectedCallbacks.Contains(value))
            Unsupported("This callable cannot be used as a JSON callback; wrap the operation in an arrow function.");
        return true;
    }

    private JsValue ParseJson(JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsString()) throw new CodeModeDiagnosticException(new("ExecutionFailure", "JSON.parse expects a string."));
        var text = args[0].AsString();
        if (Encoding.UTF8.GetByteCount(text) > limits.MaxBoundaryBytes) Invalid("JSON input exceeds the boundary byte limit.");
        JsValue parsed;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            // Validate BEFORE the reviver: filtering a blocked key must not make
            // the original payload acceptable (source copyIn ordering).
            parsed = FromJson(engine, CodeModeData.Copy(document.RootElement, limits.MaxBoundaryBytes, "JSON.parse result"));
        }
        catch (Exception error) when (error is JsonException or CodeModeDiagnosticException)
        { throw new JavaScriptException(Error(engine, new("ExecutionFailure", "JSON.parse received invalid JSON: " + error.Message), "SyntaxError")); }
        var reviver = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (!JsonCallback(reviver)) return parsed;
        return Visit("", parsed, 0);

        JsValue Visit(string key, JsValue value, int depth)
        {
            checkpoint();
            if (depth > 32) Invalid("JSON reviver traversal exceeds the maximum depth of 32.");
            if (value is ArrayInstance array)
            {
                var length = (uint)Own(array, "length").AsNumber();
                for (uint index = 0; index < length; index++) Update(array, index.ToString(CultureInfo.InvariantCulture), depth);
            }
            else if (value is JsObject obj)
                foreach (var name in JsonKeys(obj)) Update(obj, name, depth);
            // No this-holder is supplied, matching the source callback algebra.
            // An async callback's promise remains a value; do not auto-await it.
            return engine.Call(reviver, JsValue.Undefined, new JsValue[] { new JsString(key), value });
        }

        void Update(ObjectInstance holder, string key, int depth)
        {
            var revived = Visit(key, Own(holder, key), depth + 1);
            if (revived.IsUndefined()) holder.RemoveOwnProperty(key);
            else holder.Set(key, revived, false);
        }
    }

    private JsValue StringifyJson(JsValue[] args)
    {
        var input = args.Length > 0 ? args[0] : JsValue.Undefined;
        var replacer = args.Length > 1 ? args[1] : JsValue.Undefined;
        var callable = JsonCallback(replacer);
        // This is validation, not a replacement of input: function replacers must
        // observe original leaf identities and non-finite/undefined values.
        _ = ToJson(input, true);
        var value = callable ? Transform("", input, 0) : input;
        if (value.IsUndefined()) return JsValue.Undefined;
        var space = args.Length > 2 ? args[2] : JsValue.Undefined;
        var indent = space.IsString() ? space.AsString()[..Math.Min(10, space.AsString().Length)] :
            space.IsNumber() ? new string(' ', double.IsNaN(space.AsNumber()) || space.AsNumber() <= 0 ? 0 :
                space.AsNumber() >= 10 ? 10 : (int)Math.Truncate(space.AsNumber())) : "";
        string[]? properties = null;
        if (replacer is ArrayInstance selected)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            var length = (uint)Own(selected, "length").AsNumber();
            if (length > limits.MaxBoundaryBytes) Invalid("JSON property list exceeds the item limit.");
            for (uint index = 0; index < length; index++)
            {
                checkpoint();
                var item = Own(selected, index.ToString(CultureInfo.InvariantCulture));
                if (!item.IsString() && !item.IsNumber()) continue;
                var name = item.IsString() ? item.AsString() : engine.Call(_nativeStringConvert, JsValue.Undefined, new[] { item }).AsString();
                if (name is "constructor" or "prototype" or "__proto__") Invalid("Blocked JSON property-list key: " + name);
                if (names.Add(name)) ordered.Add(name);
            }
            properties = ordered.ToArray();
        }
        var output = new StringBuilder();
        long bytes = 0;
        WriteValue(value, 0);
        return new JsString(output.ToString());

        JsValue Transform(string key, JsValue item, int depth)
        {
            checkpoint();
            if (depth > 32) Invalid("JSON replacer traversal exceeds the maximum depth of 32.");
            var replaced = engine.Call(replacer, JsValue.Undefined, new JsValue[] { new JsString(key), JsonPrimitive(item) });
            // The source omits callback-returned functions before validating the
            // replacement, while functions already inside input fail validation.
            if (replaced.IsUndefined() || replaced is Function) return JsValue.Undefined;
            _ = ToJson(replaced, true);
            if (replaced is ArrayInstance array)
            {
                var length = (uint)Own(array, "length").AsNumber();
                var values = new JsValue[length];
                for (uint index = 0; index < length; index++)
                {
                    var child = Transform(index.ToString(CultureInfo.InvariantCulture), Own(array, index.ToString(CultureInfo.InvariantCulture)), depth + 1);
                    values[index] = child.IsUndefined() ? JsValue.Null : child;
                }
                return new JsArray(engine, values);
            }
            if (replaced is JsObject obj)
            {
                var values = new List<KeyValuePair<string, JsValue>>();
                foreach (var name in JsonKeys(obj))
                {
                    var child = Transform(name, Own(obj, name), depth + 1);
                    if (!child.IsUndefined()) values.Add(new(name, child));
                }
                return JsObject.CreateFromEntries(engine, values);
            }
            return replaced.IsNumber() && !double.IsFinite(replaced.AsNumber()) ? JsValue.Null : replaced;
        }

        void Append(string text)
        {
            checkpoint();
            bytes += Encoding.UTF8.GetByteCount(text);
            if (bytes > limits.MaxBoundaryBytes) Invalid("JSON.stringify output exceeds the boundary byte limit.");
            output.Append(text);
        }

        void NewLine(int depth)
        {
            if (indent.Length == 0) return;
            Append("\n");
            for (var index = 0; index < depth; index++) Append(indent);
        }

        string Quote(JsValue primitive) => engine.Call(_nativeJsonStringify, JsValue.Undefined, new[] { primitive }).AsString();

        void WriteValue(JsValue item, int depth)
        {
            checkpoint();
            item = JsonPrimitive(item);
            if (depth > 32) Invalid("JSON output exceeds the maximum depth of 32.");
            if (item.IsUndefined() || item.IsNull() || item.IsNumber() && !double.IsFinite(item.AsNumber())) { Append("null"); return; }
            if (item.IsString() || item.IsNumber() || item.IsBoolean()) { Append(Quote(item)); return; }
            if (IsCollection(item) || IsRegex(item) || item is QueryValue) { Append("{}"); return; }
            if (item is ArrayInstance array)
            {
                var length = (uint)Own(array, "length").AsNumber();
                Append("[");
                for (uint index = 0; index < length; index++)
                {
                    if (index > 0) Append(",");
                    NewLine(depth + 1);
                    WriteValue(Own(array, index.ToString(CultureInfo.InvariantCulture)), depth + 1);
                }
                if (length > 0) NewLine(depth);
                Append("]");
                return;
            }
            if (item is not JsObject obj) { Invalid("JSON serialization requires plain data."); return; }
            Append("{");
            var written = 0;
            foreach (var name in properties ?? JsonKeys(obj))
            {
                var child = Own(obj, name);
                if (child.IsUndefined()) continue;
                if (written++ > 0) Append(",");
                NewLine(depth + 1);
                Append(Quote(new JsString(name)));
                Append(indent.Length == 0 ? ":" : ": ");
                WriteValue(child, depth + 1);
            }
            if (written > 0) NewLine(depth);
            Append("}");
        }
    }

    private string[] JsonKeys(ObjectInstance obj)
    {
        var keys = new List<string>();
        foreach (var key in obj.GetOwnPropertyKeys(Types.String))
        {
            checkpoint();
            if (!key.IsString()) Invalid("Symbol keys are unavailable in JSON data.");
            var name = key.AsString();
            if (name is "constructor" or "prototype" or "__proto__") Invalid("Blocked JSON data property: " + name);
            var descriptor = obj.GetOwnProperty(key);
            if (!descriptor.IsDataDescriptor()) Invalid("Accessor properties are unavailable in JSON data.");
            if (descriptor.Enumerable) keys.Add(name);
        }
        return keys.ToArray();
    }
}
