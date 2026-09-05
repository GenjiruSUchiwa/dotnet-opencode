namespace OpenCode.Core.CodeMode;

using System.Globalization;
using System.Text.RegularExpressions;
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
    private JsValue _dateConstructor = JsValue.Undefined;
    private JsValue _regexConstructor = JsValue.Undefined;
    private JsValue _primitiveString = JsValue.Undefined;
    private JsValue _primitiveNumber = JsValue.Undefined;
    private ObjectInstance? _datePrototype;
    private ObjectInstance? _regexPrototype;
    private static readonly HashSet<string> DateMethods = Names("getTime valueOf toISOString toJSON toString toUTCString toGMTString getFullYear getMonth getDate getDay getHours getMinutes getSeconds getMilliseconds getUTCFullYear getUTCMonth getUTCDate getUTCDay getUTCHours getUTCMinutes getUTCSeconds getUTCMilliseconds getTimezoneOffset setTime setMilliseconds setUTCMilliseconds setSeconds setUTCSeconds setMinutes setUTCMinutes setHours setUTCHours setDate setUTCDate setMonth setUTCMonth setFullYear setUTCFullYear");
    private static readonly HashSet<string> RegexProperties = Names("source flags lastIndex hasIndices global ignoreCase multiline sticky unicode unicodeSets dotAll");
    private static readonly HashSet<string> RegexMethods = Names("test exec toString");

    private void InstallPrimitives()
    {
        _primitiveString = engine.GetValue("String");
        _primitiveNumber = engine.GetValue("Number");
        _dateConstructor = engine.GetValue("Date");
        _regexConstructor = engine.GetValue("RegExp");
        _datePrototype = (ObjectInstance)_dateConstructor.Get("prototype");
        _regexPrototype = (ObjectInstance)_regexConstructor.Get("prototype");
        var date = new ClrFunction(engine, "Date", (_, _) => Guest(() => DateText(engine.Construct(_dateConstructor))));
        foreach (var name in Names("now parse UTC"))
            date.DefineOwnProperty(name, new PropertyDescriptor(new ClrFunction(engine, name, (_, args) => Guest(() =>
                engine.Call(_dateConstructor.Get(name), _dateConstructor, name == "now" ? [] : name == "parse"
                    ? new JsValue[] { new JsString(ValueString(args.Length > 0 ? args[0] : JsValue.Undefined)) }
                    : args.ToArray().Select(value => (JsValue)new JsNumber(ValueNumber(value))).ToArray()))), PropertyFlag.AllForbidden));
        engine.SetValue("Date", date);
        _statics[date] = Names("now parse UTC");
        // Native default Date coercion also uses the source's ISO string, rather
        // than exposing Jint's host-locale Date.prototype.toString formatting.
        _datePrototype.DefineOwnProperty("toString", new PropertyDescriptor(new ClrFunction(engine, "toString", (receiver, _) =>
            Guest(() => IsDate(receiver) ? DateText(receiver) : Unsupported("Date receiver required."))), PropertyFlag.AllForbidden));
        var regex = new ClrFunction(engine, "RegExp", (_, args) => Guest(() => ConstructRegex(args.ToArray())));
        regex.DefineOwnProperty("escape", new PropertyDescriptor(new ClrFunction(engine, "escape", (_, args) => Guest(() =>
        {
            if (args.Length == 0 || !args[0].IsString()) return Unsupported("RegExp.escape expects a string.");
            return engine.Call(_regexConstructor.Get("escape"), _regexConstructor, new[] { args[0] });
        })), PropertyFlag.AllForbidden));
        engine.SetValue("RegExp", regex);
        _statics[regex] = Names("escape");
        var text = new ClrFunction(engine, "String", (_, args) => Guest(() =>
        {
            if (args.Length == 0) return new JsString("");
            if (errors.Brand(args[0]) is null && (args[0] is not ObjectInstance error || !_errors.Contains(error.Prototype!))) ToJson(args[0], true);
            return new JsString(ValueString(args[0]));
        }));
        foreach (var name in Names("fromCharCode fromCodePoint prototype"))
            text.DefineOwnProperty(name, new PropertyDescriptor(_primitiveString.Get(name), PropertyFlag.AllForbidden));
        engine.SetValue("String", text);
        _statics[text] = Names("fromCharCode fromCodePoint");
        engine.Global.DefineOwnProperty("__oc_newDate", new PropertyDescriptor(new ClrFunction(engine, "newDate", (_, args) =>
            Guest(() => ConstructDate(args.ToArray()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_newRegExp", new PropertyDescriptor(new ClrFunction(engine, "newRegExp", (_, args) =>
            Guest(() => ConstructRegex(args.ToArray()))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_instanceof", new PropertyDescriptor(new ClrFunction(engine, "instanceof", (_, args) => Guest(() =>
        {
            var constructor = args[1];
            var value = args[0];
            if (ReferenceEquals(constructor, engine.GetValue("Date"))) return IsDate(value) ? JsBoolean.True : JsBoolean.False;
            if (ReferenceEquals(constructor, engine.GetValue("RegExp"))) return IsRegex(value) ? JsBoolean.True : JsBoolean.False;
            if (ReferenceEquals(constructor, _mapConstructor)) return IsMap(value) ? JsBoolean.True : JsBoolean.False;
            if (ReferenceEquals(constructor, _setConstructor)) return IsSet(value) ? JsBoolean.True : JsBoolean.False;
            if (_errorConstructors.TryGetValue(constructor, out var name))
                return errors.Brand(value) is { } brand && (name == "Error" || brand == name) ? JsBoolean.True : JsBoolean.False;
            return UrlInstanceOf(value, constructor);
        })), PropertyFlag.AllForbidden));
    }

    private bool IsDate(JsValue value) => _datePrototype is not null && value is ObjectInstance obj && ReferenceEquals(obj.Prototype, _datePrototype);
    private bool IsRegex(JsValue value) => _regexPrototype is not null && value is ObjectInstance obj && ReferenceEquals(obj.Prototype, _regexPrototype);
    private bool IsBuiltinValue(JsValue value) => IsCollection(value) || IsDate(value) || IsRegex(value) || IsUrlValue(value);
    private JsValue DateCall(JsValue date, string method, params JsValue[] args) => engine.Call(_datePrototype!.Get(method), date, args);
    private JsValue DateBoundary(JsValue date) => double.IsFinite(DateCall(date, "getTime").AsNumber()) ? DateCall(date, "toISOString") : JsValue.Null;
    private JsValue DateText(JsValue date) => DateBoundary(date) is { } value && !value.IsNull() ? value : new JsString("Invalid Date");
    private JsValue JsonPrimitive(JsValue value) => IsDate(value) ? DateBoundary(value) : value is UrlValue url ? new JsString(url.Address.Href) : value;

    private JsValue ConstructDate(JsValue[] args)
    {
        if (args.Length == 0) return engine.Construct(_dateConstructor);
        if (args.Length == 1)
        {
            var value = IsDate(args[0]) ? DateCall(args[0], "getTime") : DatePrimitive(args[0], numeric: false);
            return engine.Construct(_dateConstructor, new[] { value.IsString() ? value : new JsNumber(ValueNumber(value)) });
        }
        return engine.Construct(_dateConstructor, args.Select(value => (JsValue)new JsNumber(ValueNumber(value))).ToArray());
    }

    private JsValue DatePrimitive(JsValue value, bool numeric)
    {
        if (value is not ObjectInstance obj || IsBuiltinValue(value) || numeric && value is ArrayInstance) return value;
        foreach (var name in new[] { "valueOf", "toString" })
        {
            if (!obj.HasOwnProperty(name))
            {
                if (name == "toString") return numeric ? value : new JsString(ValueString(value));
                continue;
            }
            var method = Own(obj, name);
            if (method is not Function) continue;
            RequireCollectionCallback(method, "Date conversion");
            var result = engine.Call(method, JsValue.Undefined, Array.Empty<JsValue>());
            if (result is not ObjectInstance) return result;
        }
        return Unsupported("Cannot convert object to primitive value.");
    }

    private JsValue ReadDate(JsValue target, string method)
    {
        if (!DateMethods.Contains(method)) return JsValue.Undefined;
        return new ClrFunction(engine, method, (_, args) => Guest(() =>
        {
            if (method == "toString") return DateText(target);
            if (method == "toJSON") return DateBoundary(target);
            if (!method.StartsWith("set", StringComparison.Ordinal)) return DateCall(target, method);
            // The source snapshots the original timestamp BEFORE argument coercion.
            var temporary = engine.Construct(_dateConstructor, new[] { DateCall(target, "getTime") });
            var count = method is "setHours" or "setUTCHours" ? 4 :
                method is "setMinutes" or "setUTCMinutes" or "setFullYear" or "setUTCFullYear" ? 3 :
                method is "setSeconds" or "setUTCSeconds" or "setMonth" or "setUTCMonth" ? 2 : 1;
            var converted = args.ToArray().Take(count).Select(value => (JsValue)new JsNumber(ValueNumber(DatePrimitive(value, numeric: true)))).ToArray();
            var time = DateCall(temporary, method, converted);
            DateCall(target, "setTime", time);
            return time;
        }));
    }

    private string ValueString(JsValue value, int depth = 0)
    {
        checkpoint();
        if (depth > 32) { Invalid("String coercion exceeds the maximum depth."); return ""; }
        if (IsDate(value)) return DateText(value).AsString();
        if (IsRegex(value)) return "/" + value.Get("source").AsString() + "/" + value.Get("flags").AsString();
        if (TryUrlString(value, out var url)) return url;
        if (IsMap(value)) return "[object Map]";
        if (IsSet(value)) return "[object Set]";
        if (value is ObjectInstance error && (errors.Brand(value) is not null || _errors.Contains(error.Prototype!)))
        {
            var name = error.Get("name").IsString() ? error.Get("name").AsString() : "Error";
            var message = Own(error, "message").IsString() ? Own(error, "message").AsString() : "";
            return name.Length == 0 ? message : message.Length == 0 ? name : name + ": " + message;
        }
        if (value is ArrayInstance array)
        {
            var parts = new List<string>();
            var length = (uint)Own(array, "length").AsNumber();
            for (uint index = 0; index < length; index++)
            {
                var item = Own(array, index.ToString(CultureInfo.InvariantCulture));
                parts.Add(item.IsNull() || item.IsUndefined() ? "" : ValueString(item, depth + 1));
            }
            return string.Join(",", parts);
        }
        if (value is ObjectInstance) return "[object Object]";
        return engine.Call(_primitiveString, JsValue.Undefined, new[] { value }).AsString();
    }

    private double ValueNumber(JsValue value)
    {
        if (IsDate(value)) return DateCall(value, "getTime").AsNumber();
        if (value is ArrayInstance) return engine.Call(_primitiveNumber, JsValue.Undefined, new JsValue[] { new JsString(ValueString(value)) }).AsNumber();
        if (value is ObjectInstance) return double.NaN;
        return engine.Call(_primitiveNumber, JsValue.Undefined, new[] { value }).AsNumber();
    }

    private JsValue ConstructRegex(JsValue[] args)
    {
        var first = args.Length == 0 ? JsValue.Undefined : args[0];
        var pattern = IsRegex(first) ? first.Get("source").AsString() : first.IsUndefined() ? "" : ValueString(first);
        var flags = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (!flags.IsUndefined() && !flags.IsString()) throw new JavaScriptException(Error(engine, new("ExecutionFailure", "RegExp flags must be a string."), "SyntaxError"));
        var text = flags.IsUndefined() ? IsRegex(first) ? first.Get("flags").AsString() : "" : flags.AsString();
        if (text.Any(flag => !"dgimsuvy".Contains(flag)) || text.Distinct().Count() != text.Length || text.Contains('u') && text.Contains('v'))
            throw new JavaScriptException(Error(engine, new("ExecutionFailure", "Invalid RegExp flags. Valid flags are d, g, i, m, s, u, v, and y; u and v are mutually exclusive."), "SyntaxError"));
        try { return engine.Construct(_regexConstructor, new JsValue[] { new JsString(pattern), new JsString(text) }); }
        catch (JavaScriptException failure)
        { throw new JavaScriptException(Error(engine, new("ExecutionFailure", "Invalid regular expression: " + errors.Normalize(failure.Error, _ => "invalid pattern").Message), "SyntaxError")); }
    }

    private JsValue ReadRegex(JsValue target, string key)
    {
        if (RegexProperties.Contains(key)) return target.Get(key);
        if (!RegexMethods.Contains(key)) return JsValue.Undefined;
        return new ClrFunction(engine, key, (_, args) => Guest(() =>
        {
            if (key == "toString") return new JsString(ValueString(target));
            var input = new JsString(ValueString(args.Length > 0 ? args[0] : JsValue.Undefined));
            var raw = Own((ObjectInstance)target, "lastIndex");
            var number = ValueNumber(raw);
            ((ObjectInstance)target).Set("lastIndex", new JsNumber(double.IsNaN(number) || number <= 0 ? 0 : Math.Min(Math.Floor(number), 9007199254740991)), false);
            var stateful = target.Get("global").AsBoolean() || target.Get("sticky").AsBoolean();
            try
            {
                var result = engine.Call(_regexPrototype!.Get(key), target, new JsValue[] { input });
                return key == "exec" && !result.IsNull() ? RegexMatch(result) : result;
            }
            catch (RegexMatchTimeoutException) { throw new CodeModeDiagnosticException(new("TimeoutExceeded", "Regular expression execution exceeded its configured timeout.")); }
            finally { if (!stateful) ((ObjectInstance)target).Set("lastIndex", raw, false); }
        }));
    }

    private JsValue RegexMatch(JsValue raw)
    {
        var source = (ObjectInstance)raw;
        var length = (uint)Own(source, "length").AsNumber();
        var values = new JsValue[length];
        for (uint index = 0; index < length; index++) { checkpoint(); values[index] = Own(source, index.ToString(CultureInfo.InvariantCulture)); }
        var result = new JsArray(engine, values);
        foreach (var key in new[] { "index", "groups", "indices" })
        {
            var value = Own(source, key);
            if (value.IsUndefined()) continue;
            if (key == "groups") value = RegexGroups(value);
            if (key == "indices" && value is ObjectInstance indices)
            {
                value = RegexMatch(indices);
                ((ObjectInstance)value).Set("groups", Own(indices, "groups").IsUndefined() ? JsValue.Undefined : RegexGroups(Own(indices, "groups")), false);
            }
            result.Set(key, value, false);
        }
        return result;
    }

    private JsValue RegexGroups(JsValue value) => value is not ObjectInstance obj ? value : JsObject.CreateFromEntries(engine,
        obj.GetOwnPropertyKeys().Where(key => key.IsString() && key.AsString() is not ("constructor" or "prototype" or "__proto__"))
            .Select(key => new KeyValuePair<string, JsValue>(key.AsString(), Own(obj, key))));

    private JsValue ReadRegexString(JsValue receiver, string method) => new ClrFunction(engine, method, (_, arguments) => Guest(() =>
    {
        var args = arguments.ToArray();
        if (method is "match" or "matchAll" or "search")
        {
            var pattern = args.Length == 0 ? JsValue.Undefined : args[0];
            if (!pattern.IsUndefined() && !pattern.IsString() && !IsRegex(pattern)) return Unsupported("String." + method + " expects a RegExp or string pattern.");
            if (!IsRegex(pattern)) pattern = ConstructRegex(new[] { pattern, (JsValue)new JsString(method == "matchAll" ? "g" : "") });
            if (method == "matchAll" && !pattern.Get("global").AsBoolean()) return Unsupported("String.matchAll requires a global (g) regular expression.");
            args = [pattern];
        }
        if (method is "replace" or "replaceAll" && args.Length > 1 && args[1] is Function callback)
        {
            RequireCollectionCallback(callback, "String." + method);
            args[1] = new ClrFunction(engine, "replacement", (_, values) =>
            {
                checkpoint();
                var copied = values.ToArray();
                if (copied.Length > 0 && copied[^1] is JsObject) copied[^1] = RegexGroups(copied[^1]);
                return engine.Call(callback, JsValue.Undefined, copied);
            });
        }
        if (method == "replaceAll" && args.Length > 0 && IsRegex(args[0]) && !args[0].Get("global").AsBoolean())
            return Unsupported("String.replaceAll requires a global (g) regular expression.");
        if (method is "replace" or "replaceAll")
        {
            var pattern = args.Length > 0 ? args[0] : JsValue.Undefined;
            var replacement = args.Length > 1 ? args[1] : JsValue.Undefined;
            args = [IsRegex(pattern) ? pattern : new JsString(ValueString(pattern)), replacement is Function ? replacement : new JsString(ValueString(replacement))];
        }
        if (method == "split" && args.Length > 0 && !args[0].IsUndefined() && !IsRegex(args[0])) args[0] = new JsString(ValueString(args[0]));
        try
        {
            var result = engine.Call(Prototype("String").Get(method), receiver, args);
            if (method == "match" && !result.IsNull()) return RegexMatch(result);
            if (method != "matchAll") return result;
            var next = result.Get("next");
            var matches = new List<JsValue>();
            while (true)
            {
                checkpoint();
                var step = engine.Call(next, result, Array.Empty<JsValue>());
                if (step.Get("done").AsBoolean()) return new JsArray(engine, matches.ToArray());
                if (matches.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("Regex match materialization exceeds the item ceiling.");
                matches.Add(RegexMatch(step.Get("value")));
            }
        }
        catch (RegexMatchTimeoutException) { throw new CodeModeDiagnosticException(new("TimeoutExceeded", "Regular expression execution exceeded its configured timeout.")); }
    }));
}
