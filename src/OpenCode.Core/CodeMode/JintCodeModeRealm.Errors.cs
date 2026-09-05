namespace OpenCode.Core.CodeMode;

using System.Globalization;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;

internal sealed partial class JintCodeModeRealm
{
    private readonly Dictionary<JsValue, string> _errorConstructors = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> ErrorNames = Names("Error TypeError RangeError SyntaxError ReferenceError EvalError URIError AggregateError");

    private void InstallErrors()
    {
        foreach (var name in ErrorNames)
        {
            var constructor = new ClrFunction(engine, name, (_, args) => Guest(() => ConstructError(name, args.ToArray())));
            _errorConstructors[constructor] = name;
            engine.SetValue(name, constructor);
        }
        engine.Global.DefineOwnProperty("__oc_thrown", new PropertyDescriptor(new ClrFunction(engine, "thrown", (_, args) => errors.Thrown(args[0])), PropertyFlag.AllForbidden));
        engine.Global.DefineOwnProperty("__oc_invalidLiteral", new PropertyDescriptor(new ClrFunction(engine, "invalidLiteral", (_, _) =>
            Guest(() => throw new CodeModeDiagnosticException(new("InvalidDataValue", "Literal must contain data only.")))), PropertyFlag.AllForbidden));
    }

    private JsValue ConstructError(string name, JsValue[] args)
    {
        if (name != "AggregateError") return errors.Create(name, args.Length == 0 || args[0].IsUndefined() ? "" : ValueString(args[0]));
        var values = new List<JsValue>();
        var cursor = OpenCursor(args.Length == 0 ? JsValue.Undefined : args[0]);
        while (true)
        {
            var step = cursor.Next();
            if (step.Done) break;
            if (values.Count >= Math.Min(limits.MaxBoundaryBytes, 100000)) Invalid("AggregateError entries exceed the item ceiling.");
            values.Add(step.Value);
        }
        // Source constructors ignore extra options, including a native-JS cause
        // option. A caller can explicitly assign a data cause field afterwards.
        return errors.Create(name, args.Length < 2 || args[1].IsUndefined() ? "" : ValueString(args[1]), new JsArray(engine, values.ToArray()));
    }

    internal CodeModeDiagnostic NormalizeFailure(JsValue value) => errors.Normalize(value, ProgramThrowMessage);

    private string ProgramThrowMessage(JsValue value)
    {
        var pending = new Stack<JsValue>();
        var seen = new HashSet<JsValue>(ReferenceEqualityComparer.Instance);
        var work = 0;
        var maximum = Math.Min(limits.MaxBoundaryBytes, 100000);
        pending.Push(value);
        while (pending.TryPop(out var item))
        {
            if (++work > maximum) return "[diagnostic data limit exceeded]";
            if (IsBuiltinValue(item) || item is ObjectInstance obj && !PlainWritable(obj)) return "a non-data value";
            if (item is not ObjectInstance data || !seen.Add(item)) continue;
            if (data is ArrayInstance)
            {
                var length = (uint)Own(data, "length").AsNumber();
                for (uint index = 0; index < length; index++) pending.Push(Own(data, index.ToString(CultureInfo.InvariantCulture)));
            }
            else foreach (var key in data.GetOwnPropertyKeys(Types.String))
                if (data.GetOwnProperty(key).Enumerable) pending.Push(Own(data, key));
        }
        if (value.IsString()) return CodeModeData.Truncate(value.AsString(), limits.MaxBoundaryBytes);
        if (value is ObjectInstance message && Own(message, "message") is { } text && text.IsString())
            return CodeModeData.Truncate(text.AsString(), limits.MaxBoundaryBytes);
        if (value.IsUndefined()) return "undefined";
        // No engine.Call, ToObject, getter, toJSON or guest coercion is permitted
        // while formatting a rejection observer. Those could re-enter the job queue.
        var output = new StringBuilder();
        var stack = new HashSet<JsValue>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        try { Write(value, 0); return output.ToString(); }
        catch (CodeModeDiagnosticException) { return value is ObjectInstance ? "[object Object]" : "a non-data value"; }

        void Append(string part)
        {
            if (++work > maximum) Invalid("Thrown data exceeds the diagnostic work limit.");
            bytes += Encoding.UTF8.GetByteCount(part);
            if (bytes > limits.MaxBoundaryBytes) Invalid("Thrown data exceeds the diagnostic byte limit.");
            output.Append(part);
        }
        void Quote(string part)
        {
            Append("\"");
            for (var index = 0; index < part.Length; index++)
            {
                var ch = part[index];
                switch (ch)
                {
                    case '"': Append("\\\""); break;
                    case '\\': Append("\\\\"); break;
                    case '\b': Append("\\b"); break;
                    case '\f': Append("\\f"); break;
                    case '\n': Append("\\n"); break;
                    case '\r': Append("\\r"); break;
                    case '\t': Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(ch) && index + 1 < part.Length && char.IsLowSurrogate(part[index + 1])) Append(part.Substring(index++, 2));
                        else if (ch < 32 || char.IsSurrogate(ch)) Append("\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else Append(ch.ToString());
                        break;
                }
            }
            Append("\"");
        }
        void Write(JsValue item, int depth)
        {
            if (++work > maximum) Invalid("Thrown data exceeds the diagnostic work limit.");
            if (depth > 32) Invalid("Thrown data exceeds the diagnostic depth limit.");
            if (item.IsNull() || item.IsUndefined()) { Append("null"); return; }
            if (item.IsBoolean()) { Append(item.AsBoolean() ? "true" : "false"); return; }
            if (item.IsString()) { Quote(item.AsString()); return; }
            if (item.IsNumber()) { Append(DiagnosticNumber(item.AsNumber())); return; }
            if (item is not ObjectInstance data) { Invalid("Thrown data cannot be serialized."); return; }
            if (!stack.Add(data)) Invalid("Thrown data is circular.");
            if (data is ArrayInstance)
            {
                Append("[");
                var length = (uint)Own(data, "length").AsNumber();
                for (uint index = 0; index < length; index++)
                {
                    if (index > 0) Append(",");
                    Write(Own(data, index.ToString(CultureInfo.InvariantCulture)), depth + 1);
                }
                Append("]");
            }
            else
            {
                Append("{");
                var count = 0;
                foreach (var key in data.GetOwnPropertyKeys(Types.String))
                {
                    if (!data.GetOwnProperty(key).Enumerable) continue;
                    var field = Own(data, key);
                    if (field.IsUndefined()) continue;
                    if (count++ > 0) Append(",");
                    Quote(key.AsString()); Append(":"); Write(field, depth + 1);
                }
                Append("}");
            }
            stack.Remove(data);
        }
    }

    private static string DiagnosticNumber(double value)
    {
        if (!double.IsFinite(value)) return "null";
        if (value == 0) return "0";
        var negative = value < 0 ? "-" : "";
        var parts = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture).Split('E');
        var point = parts[0].IndexOf('.');
        var digits = parts[0].Replace(".", "");
        var position = (point < 0 ? digits.Length : point) + (parts.Length == 1 ? 0 : int.Parse(parts[1], CultureInfo.InvariantCulture));
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; position--; }
        if (position > 0 && position <= 21) return negative + (position >= digits.Length ? digits.PadRight(position, '0') : digits.Insert(position, "."));
        if (position <= 0 && position > -6) return negative + "0." + new string('0', -position) + digits;
        var exponent = position - 1;
        return negative + digits[0] + (digits.Length == 1 ? "" : "." + digits[1..]) + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
