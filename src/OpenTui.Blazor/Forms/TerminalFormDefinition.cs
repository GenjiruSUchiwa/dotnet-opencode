namespace OpenTui.Blazor.Forms;

using System.Collections.Immutable;

public enum TerminalFormFieldKind { String, Number, Integer, Boolean, Multiselect, External }

/// <summary>UI values, independent of any application or wire contract. Absence is represented by no dictionary entry.</summary>
public abstract record TerminalFormValue
{
    private protected TerminalFormValue() { }
    public sealed record Text(string Value) : TerminalFormValue;
    public sealed record Number(double Value) : TerminalFormValue;
    public sealed record Boolean(bool Value) : TerminalFormValue;
    public sealed record Strings(ImmutableArray<string> Value) : TerminalFormValue;
}

public sealed record TerminalFormOption(TerminalFormValue Value, string Label, string? Description = null);
public sealed record TerminalFormCondition(string Key, bool Equal, TerminalFormValue Value);

public sealed record TerminalFormField
{
    public required string Key { get; init; }
    public required TerminalFormFieldKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public ImmutableArray<TerminalFormCondition> When { get; init; } = [];
    public TerminalFormValue? Default { get; init; }
    // Null distinguishes free text from an explicitly empty choice list.
    public ImmutableArray<TerminalFormOption>? Options { get; init; }
    public bool Custom { get; init; }
    public string? Format { get; init; }
    public string? Pattern { get; init; }
    public string? Placeholder { get; init; }
    public double? MinLength { get; init; }
    public double? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? MinItems { get; init; }
    public double? MaxItems { get; init; }
    public string? Url { get; init; }
    public string Label => Title ?? (Kind == TerminalFormFieldKind.External ? Url ?? Key : Key);
    public bool Textual => Kind is TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer
        || Kind == TerminalFormFieldKind.String && Options is null;
    public bool AllowsCustom => Custom && (Kind == TerminalFormFieldKind.Multiselect
        || Kind == TerminalFormFieldKind.String && Options is not null);

    public ImmutableArray<TerminalFormOption> Choices => Kind == TerminalFormFieldKind.Boolean
        ? [new(new TerminalFormValue.Boolean(true), "Yes"), new(new TerminalFormValue.Boolean(false), "No")]
        : Options ?? [];
}

/// <summary>Semantic colors supplied by the application theme; this library does not choose a palette.</summary>
public sealed record TerminalFormTheme(
    string Text, string Subdued, string Background, string Border,
    string FieldText, string FieldFocusedText, string FieldSelectedText, string FieldFocusedBackground,
    string FieldSelectedBackground, string ActionText, string Success, string Error);
