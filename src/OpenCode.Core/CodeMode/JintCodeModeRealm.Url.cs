namespace OpenCode.Core.CodeMode;

using System.Net;
using System.Text;
using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private JsValue _iteratorSymbol = JsValue.Undefined;
    private JsValue _toPrimitiveSymbol = JsValue.Undefined;
    private static readonly HashSet<string> UrlProperties = Names("href origin protocol username password host hostname port pathname search hash searchParams");
    private static readonly HashSet<string> UrlWrites = Names("href protocol username password host hostname port pathname search hash");
    private static readonly HashSet<string> QueryMethods = Names("append delete get getAll has set sort forEach keys values entries toString");

    private sealed class UrlValue : ObjectInstance
    {
        internal Url Address;
        internal QueryValue Query;
        internal UrlValue(Engine engine, Url address) : base(engine)
        {
            Address = address;
            Query = new QueryValue(engine, this);
            Prototype = null;
        }
    }

    private sealed class QueryValue : ObjectInstance
    {
        internal readonly List<KeyValuePair<string, string>> Items = [];
        internal readonly UrlValue? Owner;
        internal string? Observed;
        internal QueryValue(Engine engine, UrlValue? owner = null) : base(engine) { Owner = owner; Prototype = null; }
    }

    private void InstallUrls()
    {
        var url = new ClrFunction(engine, "URL", (_, _) => Unsupported("URL requires new."));
        foreach (var name in Names("parse canParse"))
            url.DefineOwnProperty(name, new PropertyDescriptor(new ClrFunction(engine, name, (_, args) => Guest(() =>
            {
                if (args.Length == 0) return Unsupported("URL." + name + " requires a URL argument.");
                var parsed = ParseUrl(args.ToArray());
                return name == "canParse" ? parsed is null ? JsBoolean.False : JsBoolean.True : parsed is null ? JsValue.Null : WrapUrl(parsed);
            })), PropertyFlag.AllForbidden));
        engine.SetValue("URL", url);
        _statics[url] = Names("parse canParse");
        var query = new ClrFunction(engine, "URLSearchParams", (_, _) => Unsupported("URLSearchParams requires new."));
        engine.SetValue("URLSearchParams", query);
        _statics[query] = [];
        engine.Global.DefineOwnProperty("__oc_newURL", new PropertyDescriptor(new ClrFunction(engine, "newURL", (_, args) => Guest(() =>
            ParseUrl(args.ToArray()) is { } parsed ? WrapUrl(parsed) : throw new JavaScriptException(Error(engine, new("ExecutionFailure", "Invalid URL or base URL."), "TypeError")))), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_newURLSearchParams", new PropertyDescriptor(new ClrFunction(engine, "newURLSearchParams", (_, args) =>
            Guest(() => ConstructQuery(args.Length == 0 ? JsValue.Undefined : args[0]))), PropertyFlag.AllForbidden));
    }

    private bool IsUrlValue(JsValue value) => value is UrlValue or QueryValue;
    private bool TryUrlString(JsValue value, out string text)
    {
        text = value is UrlValue url ? url.Address.Href : value is QueryValue query ? QueryText(query) : "";
        return IsUrlValue(value);
    }

    private string UrlArgument(JsValue value)
    {
        _ = ToJson(value, true); // Validate without exposing or flattening runtime values.
        return Usv(ValueString(value));
    }

    private Url? ParseUrl(JsValue[] args)
    {
        if (args.Length == 0) return null;
        var input = UrlArgument(args[0]);
        var basis = args.Length > 1 && !args[1].IsUndefined() ? new Url(UrlArgument(args[1])) : null;
        if (basis is not null && (basis.IsInvalid || !basis.IsAbsolute)) return null;
        var result = basis is null ? new Url(input) : new Url(basis, input);
        return result.IsInvalid || !result.IsAbsolute ? null : result;
    }

    private JsValue WrapUrl(Url address)
    {
        var value = new UrlValue(engine, address);
        InstallQueryIterator(value.Query);
        value.DefineOwnProperty("toString", new PropertyDescriptor(new ClrFunction(engine, "toString", (_, _) => new JsString(value.Address.Href)), PropertyFlag.AllForbidden));
        value.DefineOwnProperty(_toPrimitiveSymbol, new PropertyDescriptor(new ClrFunction(engine, "primitive", (_, args) =>
            args.Length > 0 && args[0].IsString() && args[0].AsString() == "number" ? new JsNumber(double.NaN) : new JsString(value.Address.Href)), PropertyFlag.AllForbidden));
        return value;
    }

    private JsValue ReadUrl(JsValue value, string key)
    {
        if (value is QueryValue query)
        {
            RefreshQuery(query);
            if (key == "size") return new JsNumber(query.Items.Count);
            return QueryMethods.Contains(key) ? new ClrFunction(engine, key, (_, args) => Guest(() => QueryMethod(query, key, args.ToArray()))) : JsValue.Undefined;
        }
        var target = (UrlValue)value;
        if (key is "toString" or "toJSON") return new ClrFunction(engine, key, (_, _) => new JsString(target.Address.Href));
        if (!UrlProperties.Contains(key)) return JsValue.Undefined;
        if (key == "searchParams") return target.Query;
        return new JsString(key switch
        {
            "href" => target.Address.Href, "origin" => target.Address.Origin ?? "null", "protocol" => target.Address.Protocol,
            "username" => target.Address.UserName, "password" => target.Address.Password, "host" => target.Address.Host,
            "hostname" => target.Address.HostName, "port" => target.Address.Port, "pathname" => target.Address.PathName,
            "search" => target.Address.Search, "hash" => target.Address.Hash, _ => ""
        } ?? "");
    }

    private JsValue UrlSetter(JsValue value, string key)
    {
        if (value is not UrlValue url || !UrlWrites.Contains(key)) return Unsupported("Only RegExp.lastIndex and writable URL fields may be assigned.");
        return new ClrFunction(engine, "setURL", (_, args) => Guest(() =>
        {
            var text = UrlArgument(args[0]);
            var changed = new Url(url.Address);
            switch (key)
            {
                case "href": changed = new Url(text); break;
                case "protocol": changed.Protocol = text; break;
                case "username": changed.UserName = text; break;
                case "password": changed.Password = text; break;
                case "host": changed.Host = text; break;
                case "hostname": changed.HostName = text; break;
                case "port": changed.Port = text; break;
                case "pathname": changed.PathName = text; break;
                case "search": changed.Search = text; break;
                case "hash": changed.Hash = text; break;
            }
            if (changed.IsInvalid || !changed.IsAbsolute) return Unsupported("URL." + key + " received an invalid value.");
            url.Address = changed;
            return args[0];
        }));
    }

    private JsValue UrlInstanceOf(JsValue value, JsValue constructor)
    {
        if (ReferenceEquals(constructor, engine.GetValue("URL"))) return value is UrlValue ? JsBoolean.True : JsBoolean.False;
        if (ReferenceEquals(constructor, engine.GetValue("URLSearchParams"))) return value is QueryValue ? JsBoolean.True : JsBoolean.False;
        return Unsupported("instanceof currently requires Map, Set, Date, RegExp, URL, or URLSearchParams.");
    }

    private QueryValue ConstructQuery(JsValue input)
    {
        var value = new QueryValue(engine);
        if (input is QueryValue previous) { RefreshQuery(previous); value.Items.AddRange(previous.Items); }
        else if (input.IsUndefined()) { }
        else if (input.IsString() || input.IsNull() || input.IsBoolean() || input.IsNumber()) ParseQuery(value, Usv(ValueString(input)));
        else if (input is Jint.Native.Array.ArrayInstance or GeneratorValue || IsCollection(input) || HasCustomIterator(input))
        {
            ConsumeIterable(input, item =>
            {
                var pair = new List<string>();
                ConsumeIterable(item, part =>
                {
                    if (pair.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("URLSearchParams pair exceeds the item ceiling.");
                    pair.Add(UrlArgument(part));
                });
                if (pair.Count != 2) Unsupported("URLSearchParams expects iterable [name, value] pairs.");
                AddQuery(value, pair[0], pair[1]);
            });
        }
        else
        {
            _ = ToJson(input, true);
            if (input is not JsObject obj || _tools.ContainsKey(obj) || _statics.ContainsKey(obj))
                throw new CodeModeDiagnosticException(new("InvalidDataValue", "URLSearchParams expects a string, plain data object, or supported iterable pairs."));
            foreach (var key in JsonKeys(obj)) AddQuery(value, Usv(key), UrlArgument(Own(obj, key)));
        }
        InstallQueryIterator(value);
        return value;
    }

    private void InstallQueryIterator(QueryValue value)
    {
        value.DefineOwnProperty("toString", new PropertyDescriptor(new ClrFunction(engine, "toString", (_, _) => Guest(() => new JsString(QueryText(value)))), PropertyFlag.AllForbidden));
        value.DefineOwnProperty(_toPrimitiveSymbol, new PropertyDescriptor(new ClrFunction(engine, "primitive", (_, args) => Guest(() =>
            args.Length > 0 && args[0].IsString() && args[0].AsString() == "number" ? new JsNumber(double.NaN) : new JsString(QueryText(value)))), PropertyFlag.AllForbidden));
        value.DefineOwnProperty(_iteratorSymbol, new PropertyDescriptor(new ClrFunction(engine, "iterator", (_, _) =>
        {
            var index = 0;
            var finished = false;
            var iterator = new JsObject(engine);
            iterator.Set("next", new ClrFunction(engine, "next", (_, _) => Guest(() =>
            {
                RefreshQuery(value);
                if (finished || index >= value.Items.Count)
                {
                    finished = true;
                    return JsObject.CreateFromEntries(engine, new Dictionary<string, JsValue> { ["done"] = JsBoolean.True });
                }
                var item = value.Items[index++];
                return JsObject.CreateFromEntries(engine, new Dictionary<string, JsValue> { ["done"] = JsBoolean.False,
                    ["value"] = new JsArray(engine, new JsValue[] { new JsString(item.Key), new JsString(item.Value) }) });
            })), false);
            return iterator;
        }), PropertyFlag.AllForbidden));
    }

    private JsValue QueryMethod(QueryValue value, string name, JsValue[] args)
    {
        RefreshQuery(value);
        if (name == "toString") return new JsString(QueryText(value));
        if (name is "keys" or "values" or "entries") return new JsArray(engine, value.Items.Select(item => name == "entries"
            ? (JsValue)new JsArray(engine, new JsValue[] { new JsString(item.Key), new JsString(item.Value) }) : new JsString(name == "keys" ? item.Key : item.Value)).ToArray());
        if (name == "forEach")
        {
            RequireCollectionCallback(args.Length == 0 ? JsValue.Undefined : args[0], "URLSearchParams.forEach");
            foreach (var item in value.Items.ToArray()) { checkpoint(); engine.Call(args[0], JsValue.Undefined, new JsValue[] { new JsString(item.Value), new JsString(item.Key), value }); }
            return JsValue.Undefined;
        }
        if (name == "sort")
        {
            var sorted = value.Items.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
            value.Items.Clear(); value.Items.AddRange(sorted); CommitQuery(value); return JsValue.Undefined;
        }
        var required = name is "append" or "set" ? 2 : 1;
        if (args.Length < required) return Unsupported("URLSearchParams." + name + " requires " + required + " argument(s).");
        var key = UrlArgument(args[0]);
        var text = required == 2 || (name is "has" or "delete") && args.Length > 1 && !args[1].IsUndefined() ? UrlArgument(args[1]) : null;
        if (name == "get") return value.Items.FindIndex(item => item.Key == key) is var at && at >= 0 ? new JsString(value.Items[at].Value) : JsValue.Null;
        if (name == "getAll") return new JsArray(engine, value.Items.Where(item => item.Key == key).Select(item => (JsValue)new JsString(item.Value)).ToArray());
        if (name == "has") return value.Items.Any(item => item.Key == key && (text is null || item.Value == text)) ? JsBoolean.True : JsBoolean.False;
        if (name == "append") AddQuery(value, key, text!);
        if (name == "delete") value.Items.RemoveAll(item => { checkpoint(); return item.Key == key && (text is null || item.Value == text); });
        if (name == "set")
        {
            var index = value.Items.FindIndex(item => item.Key == key);
            if (index < 0) AddQuery(value, key, text!);
            else
            {
                value.Items[index] = new(key, text!);
                for (var next = value.Items.Count - 1; next > index; next--)
                {
                    checkpoint();
                    if (value.Items[next].Key == key) value.Items.RemoveAt(next);
                }
            }
        }
        CommitQuery(value);
        return JsValue.Undefined;
    }

    private void AddQuery(QueryValue value, string key, string text)
    {
        checkpoint();
        if (value.Items.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("URLSearchParams exceeds the item ceiling.");
        value.Items.Add(new(key, text));
    }
    private static string Usv(string text) => string.Concat(text.EnumerateRunes().Select(rune => rune.ToString()));
    private void RefreshQuery(QueryValue value)
    {
        checkpoint();
        if (value.Owner is null || value.Observed == value.Owner.Address.Search) return;
        value.Observed = value.Owner.Address.Search;
        value.Items.Clear(); ParseQuery(value, value.Observed ?? "");
    }
    private void ParseQuery(QueryValue value, string query)
    {
        if (query.StartsWith('?')) query = query[1..];
        foreach (var part in query.Split('&'))
        {
            checkpoint(); if (part.Length == 0) continue;
            var separator = part.IndexOf('=');
            AddQuery(value, WebUtility.UrlDecode(separator < 0 ? part : part[..separator]), WebUtility.UrlDecode(separator < 0 ? "" : part[(separator + 1)..]));
        }
    }
    private string QueryText(QueryValue value)
    {
        RefreshQuery(value);
        var output = new StringBuilder();
        foreach (var item in value.Items)
        {
            checkpoint(); if (output.Length > 0) output.Append('&');
            Encode(item.Key); output.Append('='); Encode(item.Value);
        }
        return output.ToString();
        void Encode(string text)
        {
            foreach (var part in Encoding.UTF8.GetBytes(Usv(text)))
            {
                checkpoint();
                if (part is >= 65 and <= 90 or >= 97 and <= 122 or >= 48 and <= 57 or 42 or 45 or 46 or 95) output.Append((char)part);
                else if (part == 32) output.Append('+');
                else output.Append('%').Append(part.ToString("X2"));
                if (output.Length > limits.MaxBoundaryBytes) Invalid("URLSearchParams serialization exceeds the byte limit.");
            }
        }
    }
    private void CommitQuery(QueryValue value)
    {
        if (value.Owner is null) return;
        var text = QueryText(value);
        value.Owner.Address.Search = text.Length == 0 ? "" : "?" + text;
        value.Observed = value.Owner.Address.Search;
    }
}
