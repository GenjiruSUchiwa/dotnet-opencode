namespace OpenTui.Blazor.Forms;

using System.Globalization;
using System.Text.RegularExpressions;

public static class TerminalFormValidation
{
    public static string? Validate(TerminalFormField field, TerminalFormValue? value)
    {
        if (field.Kind == TerminalFormFieldKind.External)
            return value is TerminalFormValue.Boolean { Value: true } ? null : "Acknowledgement required";
        if (value is null) return field.Required ? "Answer required" : null;
        if (field.Required && value is TerminalFormValue.Text { Value.Length: 0 }) return "Answer required";
        if (field.Required && value is TerminalFormValue.Strings { Value.Length: 0 }) return "Select at least one option";
        if (field.Kind == TerminalFormFieldKind.String)
        {
            if (value is not TerminalFormValue.Text text) return "Expected text";
            if (field.MinLength is { } min && text.Value.Length < min) return $"Must be at least {min} characters";
            if (field.MaxLength is { } max && text.Value.Length > max) return $"Must be at most {max} characters";
            if (field.Pattern is { } pattern)
            {
                try
                {
                    if (!Regex.IsMatch(text.Value, pattern, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1)))
                        return $"Must match pattern: {pattern}";
                }
                catch (ArgumentException) { return $"Invalid pattern: {pattern}"; }
                catch (RegexMatchTimeoutException) { return "Pattern validation timed out"; }
            }
            if (field.Format == "email" && !Regex.IsMatch(text.Value, @"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                return "Expected an email address";
            if (field.Format == "uri" && !Uri.TryCreate(text.Value, UriKind.Absolute, out _)) return "Expected a URL";
            if (field.Format == "date" && !DateOnly.TryParseExact(text.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return "Expected a date (YYYY-MM-DD)";
            if (field.Format == "date-time" && !DateTimeOffset.TryParse(text.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return "Expected a date and time";
            if (field.Options is { } options && !field.Custom && !options.Any(option => option.Value == value))
                return "Select an available option";
            return null;
        }
        if (field.Kind is TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer)
        {
            if (value is not TerminalFormValue.Number number || !double.IsFinite(number.Value)) return "Expected a number";
            if (field.Kind == TerminalFormFieldKind.Integer && Math.Truncate(number.Value) != number.Value) return "Expected an integer";
            if (field.Minimum is { } min && number.Value < min) return $"Must be at least {min}";
            if (field.Maximum is { } max && number.Value > max) return $"Must be at most {max}";
            return null;
        }
        if (field.Kind == TerminalFormFieldKind.Boolean) return value is TerminalFormValue.Boolean ? null : "Expected yes or no";
        if (value is not TerminalFormValue.Strings strings) return "Expected selections";
        if (field.MinItems is { } minItems && strings.Value.Length < minItems) return $"Select at least {minItems}";
        if (field.MaxItems is { } maxItems && strings.Value.Length > maxItems) return $"Select at most {maxItems}";
        if (!field.Custom && strings.Value.Any(item => !field.Choices.Any(option => option.Value == new TerminalFormValue.Text(item))))
            return "Select only available options";
        return null;
    }

    public static string Display(TerminalFormField field, TerminalFormValue? value) => value switch
    {
        null => "",
        TerminalFormValue.Strings strings => strings.Value.IsEmpty ? "(none)"
            : string.Join(", ", strings.Value.Select(item => Display(field, new TerminalFormValue.Text(item)))),
        _ => field.Choices.FirstOrDefault(option => option.Value == value)?.Label ?? Raw(value)
    };

    public static string Raw(TerminalFormValue? value) => value switch
    {
        TerminalFormValue.Text text => text.Value,
        TerminalFormValue.Number number => number.Value.ToString(CultureInfo.InvariantCulture),
        TerminalFormValue.Boolean boolean => boolean.Value ? "true" : "false",
        _ => ""
    };
}
