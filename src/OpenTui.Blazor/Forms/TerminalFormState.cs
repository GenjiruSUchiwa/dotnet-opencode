namespace OpenTui.Blazor.Forms;

using System.Collections.Immutable;
using System.Globalization;

/// <summary>Local answers and navigation only. No service access and no implicit submission.</summary>
public sealed class TerminalFormState
{
    private readonly ImmutableArray<TerminalFormField> _fields;
    private readonly Dictionary<string, TerminalFormValue> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _custom = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externalReady = new(StringComparer.Ordinal);
    public ImmutableArray<TerminalFormField> Visible { get; private set; } = [];
    public int Tab { get; private set; }
    public int Selected { get; private set; }
    public bool Editing { get; private set; }
    public string? Error { get; private set; }
    public TerminalFormEditor Editor { get; } = new();
    public TerminalFormField? Current => Visible.ElementAtOrDefault(Tab);
    public bool Single => Visible.Length == 1 && (Visible[0].Kind == TerminalFormFieldKind.Boolean
        || Visible[0].Kind == TerminalFormFieldKind.String && Visible[0].Options is not null);
    public bool Review => !Single && Tab >= Visible.Length;
    public bool InputActive => !Review && (Editing || Current?.Textual == true);
    public bool Other => Current?.AllowsCustom == true && Selected == Rows.Length;
    public int TabCount => Single ? 1 : Visible.Length + 1;
    public int Completed => Visible.Count(item => Value(item.Key) is not null && TerminalFormValidation.Validate(item, Value(item.Key)) is null);

    public TerminalFormState(ImmutableArray<TerminalFormField> fields)
    {
        if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Length)
            throw new ArgumentException("Form fields must have unique keys.", nameof(fields));
        _fields = fields;
        foreach (var field in fields)
        {
            // Only descriptor defaults are adopted. Missing answers, including booleans, stay absent.
            if (field.Kind == TerminalFormFieldKind.External || field.Default is not { } value) continue;
            _answers[field.Key] = value;
            if (field.Kind == TerminalFormFieldKind.String && field.AllowsCustom && value is TerminalFormValue.Text text
                && !field.Choices.Any(option => option.Value == value)) _custom[field.Key] = text.Value;
        }
        RefreshVisible();
        SelectTab(0);
    }

    public TerminalFormValue? Value(string key) => _answers.GetValueOrDefault(key);
    public string CustomText(string key) => _custom.GetValueOrDefault(key, "");
    public bool ExternalReady(string key) => _externalReady.Contains(key);
    public void MarkExternalReady(string key) => _externalReady.Add(key);
    public void SetError(string? error) => Error = error;
    public bool Picked(TerminalFormValue value) => Value(Current?.Key ?? "") is TerminalFormValue.Strings strings
        ? value is TerminalFormValue.Text text && strings.Value.Contains(text.Value, StringComparer.Ordinal) : Value(Current?.Key ?? "") == value;

    public ImmutableArray<TerminalFormOption> Rows
    {
        get
        {
            if (Current is not { } current) return [];
            if (current.Kind != TerminalFormFieldKind.Multiselect || Value(current.Key) is not TerminalFormValue.Strings strings)
                return current.Choices;
            return current.Choices.AddRange(strings.Value.Where(item => item != CustomText(current.Key)
                && !current.Choices.Any(option => option.Value == new TerminalFormValue.Text(item)))
                .Select(item => new TerminalFormOption(new TerminalFormValue.Text(item), item)));
        }
    }

    public void SelectTab(int index)
    {
        Tab = Math.Clamp(index, 0, TabCount - 1);
        Editing = false;
        Error = null;
        Selected = 0;
        if (Current is not { } field) { Editor.Reset(""); return; }
        var selected = Array.FindIndex(field.Choices.ToArray(), option => option.Value == Value(field.Key));
        Selected = selected >= 0 ? selected : Value(field.Key) is TerminalFormValue.Text && field.AllowsCustom ? Rows.Length : 0;
        Editor.Reset(field.Textual ? TerminalFormValidation.Raw(Value(field.Key)) : CustomText(field.Key));
    }

    public bool MoveTab(int direction)
    {
        if (InputActive && !CommitInput())
        {
            if (direction < 0) SelectTab((Tab - 1 + TabCount) % TabCount);
            return false;
        }
        SelectTab((Tab + direction + TabCount) % TabCount);
        return true;
    }

    public void MoveOption(int direction)
    {
        var count = Rows.Length + (Current?.AllowsCustom == true ? 1 : 0);
        if (count > 0) Selected = (Selected + direction + count) % count;
    }

    /// <returns>True only when an explicit choice has completed a single-choice form.</returns>
    public bool Choose(int? index = null)
    {
        if (Current is not { } field) return false;
        if (index is { } position)
        {
            if (position < 0 || position >= Rows.Length + (field.AllowsCustom ? 1 : 0)) return false;
            Selected = position;
        }
        if (Other)
        {
            var custom = CustomText(field.Key);
            if (field.Kind == TerminalFormFieldKind.Multiselect && custom.Length > 0 && Picked(new TerminalFormValue.Text(custom)))
                Toggle(custom);
            else BeginCustom();
            return false;
        }
        if (Rows.ElementAtOrDefault(Selected) is not { } row) return false;
        if (field.Kind == TerminalFormFieldKind.Multiselect && row.Value is TerminalFormValue.Text text)
        {
            Toggle(text.Value);
            return false;
        }
        Error = TerminalFormValidation.Validate(field, row.Value);
        if (Error is not null) return false;
        SetAnswer(field, row.Value);
        if (Single) return true;
        SelectTab(Tab + 1);
        return false;
    }

    public void BeginCustom()
    {
        if (Current?.AllowsCustom != true) return;
        Selected = Rows.Length;
        Editing = true;
        Editor.Reset(CustomText(Current.Key));
    }

    public void CloseEdit() => Editing = false;

    public void InputChanged()
    {
        Error = null;
        if (!Editing || Current is not { } field) return;
        var previous = CustomText(field.Key);
        _custom[field.Key] = Editor.Text;
        if (field.Kind == TerminalFormFieldKind.Multiselect) ReplaceCustom(field, previous, Editor.Text);
        else SetAnswer(field, Editor.Text.Length == 0 ? null : new TerminalFormValue.Text(Editor.Text));
    }

    public bool CommitInput()
    {
        if (Current is not { } field) return false;
        var text = Editor.Text.Trim();
        if (field.Kind == TerminalFormFieldKind.Multiselect)
        {
            ReplaceCustom(field, CustomText(field.Key), text);
            _custom[field.Key] = text;
            Editing = false;
            return true;
        }
        TerminalFormValue? value = text.Length == 0 ? null : new TerminalFormValue.Text(text);
        if (text.Length > 0 && field.Kind is TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            {
                Error = "Expected a number";
                return false;
            }
            value = new TerminalFormValue.Number(number);
        }
        Error = TerminalFormValidation.Validate(field, value);
        if (Error is not null) return false;
        SetAnswer(field, value);
        _custom[field.Key] = field.Choices.Any(option => option.Value == value) ? "" : text;
        Editing = false;
        return true;
    }

    public void AcknowledgeExternal()
    {
        if (Current is not { Kind: TerminalFormFieldKind.External } field || !_externalReady.Contains(field.Key)) return;
        SetAnswer(field, new TerminalFormValue.Boolean(true));
        SelectTab(Tab + 1);
    }

    public bool TryAnswer(out ImmutableDictionary<string, TerminalFormValue> answer)
    {
        answer = ImmutableDictionary<string, TerminalFormValue>.Empty;
        foreach (var field in Visible)
        {
            Error = TerminalFormValidation.Validate(field, Value(field.Key));
            if (Error is null) continue;
            Error = $"{field.Label}: {Error}";
            return false;
        }
        answer = Visible.Where(field => _answers.ContainsKey(field.Key))
            .ToImmutableDictionary(field => field.Key, field => _answers[field.Key], StringComparer.Ordinal);
        return true;
    }

    private void Toggle(string value)
    {
        if (Current is not { } field) return;
        var values = Value(field.Key) is TerminalFormValue.Strings strings ? strings.Value : [];
        SetAnswer(field, new TerminalFormValue.Strings(values.Contains(value, StringComparer.Ordinal) ? values.Remove(value, StringComparer.Ordinal) : values.Add(value)));
    }

    private void ReplaceCustom(TerminalFormField field, string previous, string next)
    {
        var values = Value(field.Key) is TerminalFormValue.Strings strings ? strings.Value : [];
        values = values.Remove(previous, StringComparer.Ordinal);
        if (next.Length > 0 && !values.Contains(next, StringComparer.Ordinal)) values = values.Add(next);
        SetAnswer(field, new TerminalFormValue.Strings(values));
    }

    private void SetAnswer(TerminalFormField field, TerminalFormValue? value)
    {
        if (!field.Required && value is TerminalFormValue.Strings { Value.Length: 0 }) value = null;
        if (value is null) _answers.Remove(field.Key);
        else _answers[field.Key] = value;
        Error = null;
        RefreshVisible();
    }

    private void RefreshVisible()
    {
        var current = Current?.Key;
        var review = Review;
        var previous = new Dictionary<string, TerminalFormValue>(StringComparer.Ordinal);
        var visible = ImmutableArray.CreateBuilder<TerminalFormField>();
        foreach (var field in _fields)
        {
            // Conditions see only earlier visible input fields, not hidden retained answers or external acknowledgements.
            if (field.Kind != TerminalFormFieldKind.External && !field.When.All(condition =>
                previous.TryGetValue(condition.Key, out var value) && Matches(value, condition))) continue;
            visible.Add(field);
            if (field.Kind != TerminalFormFieldKind.External && Value(field.Key) is { } answer) previous[field.Key] = answer;
        }
        Visible = visible.ToImmutable();
        var retained = Array.FindIndex(Visible.ToArray(), field => field.Key == current);
        Tab = review ? TabCount - 1 : retained >= 0 ? retained : Math.Min(Tab, TabCount - 1);
    }

    private static bool Matches(TerminalFormValue value, TerminalFormCondition condition)
    {
        var hit = value is TerminalFormValue.Strings strings
            ? condition.Value is TerminalFormValue.Text text && strings.Value.Contains(text.Value, StringComparer.Ordinal)
            : value == condition.Value;
        return condition.Equal ? hit : !hit;
    }
}
