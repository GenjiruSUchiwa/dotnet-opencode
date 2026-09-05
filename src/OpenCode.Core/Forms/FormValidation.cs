namespace OpenCode.Core.Forms;

using Jint;
using AngleSharp.Dom;
using OpenCode.Schema;

/// <summary>Domain validation from packages/core/src/form.ts; Schema owns wire shape validation.</summary>
public static class FormValidation
{
    public static string? Fields(IReadOnlyList<FormField> fields)
    {
        if (fields.Count == 0) return "Form must have at least one field";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var earlier = new Dictionary<string, FormInputField>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (!keys.Add(field.Key)) return $"Duplicate form field key: {field.Key}";
            if (field is not FormInputField input) continue;
            foreach (var when in input.When ?? [])
            {
                if (!earlier.TryGetValue(when.Key, out var target))
                    return $"Form field condition must reference an earlier field: {field.Key} -> {when.Key}";
                if (When(when, target) is { } invalid) return $"{invalid}: {field.Key} -> {when.Key}";
            }
            earlier[field.Key] = input;
        }
        return null;
    }

    public static string? Answer(IReadOnlyList<FormField> fields, FormAnswer answer)
    {
        var keys = fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in answer.Keys)
            if (!keys.Contains(key)) return $"Unknown form field: {key}";
        foreach (var field in fields)
        {
            answer.TryGetValue(field.Key, out var value);
            if (field is FormExternalField)
            {
                if (value is not FormValue.Boolean { Value: true }) return $"External form field must be acknowledged: {field.Key}";
                continue;
            }
            var input = (FormInputField)field;
            var active = input.When?.All(when => Matches(when, answer.GetValueOrDefault(when.Key))) ?? true;
            if (value is null)
            {
                if (input.Required == true && active) return $"Missing required form field: {field.Key}";
                continue;
            }
            if (!active) return $"Form field is not active: {field.Key}";
            if (Value(input, value) is { } invalid) return invalid;
        }
        return null;
    }

    private static bool Matches(FormWhen when, FormValue? value)
    {
        if (value is null) return false;
        var hit = (value, when.Value) switch
        {
            (FormValue.Text text, FormConditionValue.Text expected) => text.Value == expected.Value,
            (FormValue.Strings strings, FormConditionValue.Text expected) => strings.Value.Contains(expected.Value, StringComparer.Ordinal),
            (FormValue.Number number, FormConditionValue.Number expected) => number.Value == expected.Value,
            (FormValue.Boolean boolean, FormConditionValue.Boolean expected) => boolean.Value == expected.Value,
            _ => false
        };
        return when.Op == "eq" ? hit : !hit;
    }

    private static string? When(FormWhen when, FormInputField target)
    {
        if (target is FormBooleanField)
            return when.Value is FormConditionValue.Boolean ? null : "Form field condition value must be a boolean";
        if (target is FormNumericField)
            return when.Value is FormConditionValue.Number ? null : "Form field condition value must be a number";
        if (when.Value is not FormConditionValue.Text text) return "Form field condition value must be a string";
        var options = target switch
        {
            FormMultiselectField multiple when multiple.Custom != true => multiple.Options,
            FormStringField single when single.Custom != true => single.Options,
            _ => null
        };
        return options is not null && !options.Any(option => option.Value == text.Value)
            ? "Form field condition value must be one of the field's options" : null;
    }

    private static string? Value(FormInputField field, FormValue value)
    {
        if (field is FormStringField text)
        {
            if (value is not FormValue.Text input) return $"Expected string for form field: {field.Key}";
            if (text.Required == true && input.Value.Length == 0) return $"Missing required form field: {field.Key}";
            if (input.Value.Length < text.MinLength) return $"Form field is too short: {field.Key}";
            if (input.Value.Length > text.MaxLength) return $"Form field is too long: {field.Key}";
            // Fixed expressions only, using the existing managed ECMAScript dependency.
            // Form text/patterns are data bindings, never executable source or CLR objects.
            var script = text.Pattern is not null || text.Format is not null
                ? new Engine().SetValue("value", input.Value) : null;
            if (text.Pattern is not null)
            {
                var matches = script!.SetValue("pattern", text.Pattern).Evaluate("(() => { try { return new RegExp(pattern).test(value) ? 1 : 0 } catch { return -1 } })()").AsNumber();
                if (matches == -1) return $"Form field has invalid pattern: {field.Key}";
                if (matches == 0) return $"Form field does not match pattern: {field.Key}";
            }
            if (text.Format == "email" && !script!.Evaluate(@"/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)").AsBoolean())
                return $"Expected email for form field: {field.Key}";
            if (text.Format == "uri" && new Url(input.Value) is not { IsInvalid: false, IsAbsolute: true }) return $"Expected URI for form field: {field.Key}";
            if (text.Format == "date" && !script!.Evaluate(@"(() => { if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false; const date = new Date(value + 'T00:00:00.000Z'); return !Number.isNaN(date.getTime()) && date.toISOString().slice(0, 10) === value })()").AsBoolean())
                return $"Expected date for form field: {field.Key}";
            if (text.Format == "date-time" && !script!.Evaluate("!Number.isNaN(new Date(value).getTime())").AsBoolean())
                return $"Expected date-time for form field: {field.Key}";
            if (text.Options is not null && text.Custom != true && !text.Options.Any(option => option.Value == input.Value))
                return $"Invalid option for form field: {field.Key}";
            return null;
        }
        if (field is FormNumericField numeric)
        {
            if (value is not FormValue.Number number || !double.IsFinite(number.Value)) return $"Expected number for form field: {field.Key}";
            if (field is FormIntegerField && Math.Truncate(number.Value) != number.Value) return $"Expected integer for form field: {field.Key}";
            if (number.Value < numeric.Minimum) return $"Form field is too small: {field.Key}";
            if (number.Value > numeric.Maximum) return $"Form field is too large: {field.Key}";
            return null;
        }
        if (field is FormBooleanField) return value is FormValue.Boolean ? null : $"Expected boolean for form field: {field.Key}";
        if (field is FormMultiselectField multiple)
        {
            if (value is not FormValue.Strings strings) return $"Expected string array for form field: {field.Key}";
            if (multiple.Required == true && strings.Value.Count == 0) return $"Missing required form field: {field.Key}";
            if (strings.Value.Count < multiple.MinItems) return $"Too few selections for form field: {field.Key}";
            if (strings.Value.Count > multiple.MaxItems) return $"Too many selections for form field: {field.Key}";
            if (multiple.Custom != true && strings.Value.Any(item => !multiple.Options.Any(option => option.Value == item)))
                return $"Invalid option for form field: {field.Key}";
            return null;
        }
        throw new NotSupportedException("Unknown form field type.");
    }
}
