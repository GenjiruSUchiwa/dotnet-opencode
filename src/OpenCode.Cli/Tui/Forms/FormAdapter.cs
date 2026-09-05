namespace OpenCode.Cli.Tui.Forms;

using System.Collections.Immutable;
using OpenCode.Schema;
using OpenTui.Blazor.Forms;

/// <summary>The location is part of the request identity, including forms whose owner is the literal "global".</summary>
public sealed record PendingForm(FormInfo Form, LocationRef Location);
public sealed record FormReplyRequest(string SessionId, FormId FormId, LocationRef Location, FormReply Reply);
public sealed record FormCancelRequest(string SessionId, FormId FormId, LocationRef Location);

public static class FormAdapter
{
    public static TerminalFormState Create(FormInfo form) => new(form.Fields.Select(Map).ToImmutableArray());

    public static FormReply Reply(IReadOnlyDictionary<string, TerminalFormValue> answer) =>
        new(new FormAnswer(answer.ToDictionary(pair => pair.Key, pair => MapValue(pair.Value), StringComparer.Ordinal)));

    /// <summary>Preserves delivery order. Home requests global forms at its active location, not a synthetic SessionId.</summary>
    public static PendingForm? First(IReadOnlyList<PendingForm> pending, string sessionId, LocationRef location) =>
        pending.FirstOrDefault(item => item.Form.SessionId == sessionId && item.Location == location);

    public static PendingForm? FirstForRoute(IReadOnlyList<PendingForm> pending, LocationRef location,
        SessionId? session = null, IReadOnlyList<SessionId>? descendants = null, bool childSession = false)
    {
        if (session is { } id && !childSession)
        {
            foreach (var owner in new[] { id }.Concat(descendants ?? []))
                if (pending.FirstOrDefault(item => item.Form.SessionId == owner.Value) is { } form) return form;
        }
        return First(pending, "global", location);
    }

    private static TerminalFormField Map(FormField field)
    {
        var common = new TerminalFormField
        {
            Key = field.Key,
            Title = field.Title,
            Description = field.Description,
            Kind = field switch
            {
                FormStringField => TerminalFormFieldKind.String,
                FormNumberField => TerminalFormFieldKind.Number,
                FormIntegerField => TerminalFormFieldKind.Integer,
                FormBooleanField => TerminalFormFieldKind.Boolean,
                FormMultiselectField => TerminalFormFieldKind.Multiselect,
                FormExternalField => TerminalFormFieldKind.External,
                _ => throw new ArgumentException("Unsupported form field.", nameof(field))
            },
            Required = field is FormInputField { Required: true },
            When = field is FormInputField input ? (input.When ?? []).Select(condition => new TerminalFormCondition(
                condition.Key, condition.Op == "eq", condition.Value switch
                {
                    FormConditionValue.Text text => new TerminalFormValue.Text(text.Value),
                    FormConditionValue.Number number => new TerminalFormValue.Number(number.Value),
                    FormConditionValue.Boolean boolean => new TerminalFormValue.Boolean(boolean.Value),
                    _ => throw new ArgumentException("Unsupported form condition.", nameof(field))
                })).ToImmutableArray() : []
        };
        return field switch
        {
            FormStringField text => common with
            {
                Format = text.Format, Pattern = text.Pattern, Placeholder = text.Placeholder,
                MinLength = text.MinLength, MaxLength = text.MaxLength, Custom = text.Custom == true,
                Default = text.Default is { } value ? new TerminalFormValue.Text(value) : null,
                Options = text.Options?.Select(MapOption).ToImmutableArray()
            },
            FormNumericField number => common with
            {
                Minimum = number.Minimum, Maximum = number.Maximum,
                Default = number.Default is { } value ? new TerminalFormValue.Number(value) : null
            },
            FormBooleanField boolean => common with
            {
                Default = boolean.Default is { } value ? new TerminalFormValue.Boolean(value) : null
            },
            FormMultiselectField multiple => common with
            {
                Options = multiple.Options.Select(MapOption).ToImmutableArray(), Custom = multiple.Custom == true,
                MinItems = multiple.MinItems, MaxItems = multiple.MaxItems,
                Default = multiple.Default is { } value ? new TerminalFormValue.Strings(value.ToImmutableArray()) : null
            },
            FormExternalField external => common with { Url = external.Url },
            _ => throw new ArgumentException("Unsupported form field.", nameof(field))
        };
    }

    private static TerminalFormOption MapOption(FormOption option) => new(new TerminalFormValue.Text(option.Value), option.Label, option.Description);

    private static FormValue MapValue(TerminalFormValue value) => value switch
    {
        TerminalFormValue.Text text => new FormValue.Text(text.Value),
        TerminalFormValue.Number number => new FormValue.Number(number.Value),
        TerminalFormValue.Boolean boolean => new FormValue.Boolean(boolean.Value),
        TerminalFormValue.Strings strings => new FormValue.Strings(strings.Value),
        _ => throw new ArgumentException("Unsupported form answer.", nameof(value))
    };
}
