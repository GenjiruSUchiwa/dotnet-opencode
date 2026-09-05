namespace OpenCode.Cli.Tui.Forms;

using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenTui.Blazor.Forms;

public partial class FormComposer : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public PendingForm Request { get; set; } = null!;
    [Parameter, EditorRequired] public TerminalFormTheme Theme { get; set; } = null!;
    [Parameter] public Func<FormReplyRequest, CancellationToken, Task>? OnReply { get; set; }
    [Parameter] public Func<FormCancelRequest, CancellationToken, Task>? OnCancel { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? OpenExternal { get; set; }
    [Parameter] public Func<string, CancellationToken, Task>? CopyExternal { get; set; }
    [Parameter] public Func<CancellationToken, Task<string?>>? ReadClipboard { get; set; }
    [Parameter] public string? UnavailableReason { get; set; }
    [Parameter] public int Width { get; set; } = 75;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public string FocusKey { get; set; } = "form";
    private TerminalFormState _state = new([]);
    private (string Session, FormId Id, LocationRef Location)? _identity;
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _finished;
    private bool _disposed;
    private string? _notice;
    private int _reviewOffset;
    private Func<string, int?, TerminalTextLayout>? _measure;
    private bool Locked => _busy || _finished || _disposed;
    private int ContentWidth => Math.Max(1, Width - 6);
    private int ReviewHeight => Math.Max(1, Math.Min(ReviewLines().Count, Math.Max(3, TerminalHeight - 14)));
    private bool Tabbed => _state.Visible.Sum(item => Truncate(item.Label, 24).Length + 3) + "Submit".Length + 3 <= Width - 4;
    private string? Message => Request.Form.Metadata?.TryGetValue("message", out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private string? ServiceStatus => UnavailableReason ?? (OnReply is null ? "Form replies are not connected to the server."
        : OnCancel is null ? "Form cancellation is not connected to the server." : null);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Request and Theme are actual Blazor component parameters.")]
    protected override void OnParametersSet()
    {
        ArgumentNullException.ThrowIfNull(Request);
        ArgumentNullException.ThrowIfNull(Theme);
        var identity = (Request.Form.SessionId, Request.Form.Id, Request.Location);
        if (_identity == identity) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _identity = identity;
        _state = FormAdapter.Create(Request.Form);
        _busy = _finished = false;
        _notice = null;
        _reviewOffset = 0;
    }

    private string Placeholder => _state.Current switch
    {
        { Placeholder: { } placeholder } => placeholder,
        { Format: "email" } => "name@example.com",
        { Format: "uri" } => "https://example.com",
        { Format: "date" } => "YYYY-MM-DD",
        { Format: "date-time" } => "YYYY-MM-DDTHH:MM:SSZ",
        { Kind: TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer, Minimum: { } min, Maximum: { } max } => $"{min}-{max}",
        { Kind: TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer, Minimum: { } min } => $"at least {min}",
        { Kind: TerminalFormFieldKind.Number or TerminalFormFieldKind.Integer, Maximum: { } max } => $"at most {max}",
        _ => _state.Editing ? "Type your own answer" : "Type your answer"
    };

    private string ActionLabel => _state.Review ? "submit" : _state.Current?.Kind == TerminalFormFieldKind.External
        ? _state.Value(_state.Current.Key) is TerminalFormValue.Boolean { Value: true } ? "continue"
            : _state.ExternalReady(_state.Current.Key) ? "I finished" : "open link"
        : _state.Current?.Kind == TerminalFormFieldKind.Multiselect ? _state.Editing ? "done" : "toggle"
        : _state.Single ? "submit" : "confirm";

    private string Hints => _busy ? "Sending… please wait" : _finished ? _notice ?? "Waiting for server update"
        : string.Join("  ", new[]
        {
            _state.Single ? null : "⇆ tab",
            _state.Review ? "↑↓ scroll" : !_state.InputActive && _state.Current?.Kind != TerminalFormFieldKind.External ? "↑↓ select" : null,
            $"enter {ActionLabel}",
            _state.Current?.Kind == TerminalFormFieldKind.External ? "c copy" : null,
            _state.Editing && _state.Current?.Textual != true ? "esc close" : "esc dismiss"
        }.Where(value => value is not null));

    private void SelectField(TerminalPointerEventArgs args, string? key)
    {
        args.Handled = true;
        if (Locked) return;
        var index = key is null ? _state.Visible.Length : Array.FindIndex(_state.Visible.ToArray(), item => item.Key == key);
        if (_state.InputActive && !_state.CommitInput() && index >= _state.Tab) return;
        index = key is null ? _state.Visible.Length : Array.FindIndex(_state.Visible.ToArray(), item => item.Key == key);
        _state.SelectTab(index < 0 ? _state.Visible.Length : index);
        _reviewOffset = 0;
    }

    private async Task ChooseRow(TerminalPointerEventArgs args, int index)
    {
        args.Handled = true;
        if (Locked) return;
        if (_state.Editing && index == _state.Rows.Length) return;
        if (_state.Choose(index)) await Submit(args.CancellationToken);
    }

    private Task OpenLink(TerminalPointerEventArgs args)
    {
        args.Handled = true;
        return Locked ? Task.CompletedTask : External(false, args.CancellationToken, acknowledge: false);
    }

    public async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        _measure = args.Measure;
        if (Locked) return;
        var key = args.Key;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        if (key.Key == ConsoleKey.Escape || control && key.Key == ConsoleKey.C)
        {
            if (control && _state.InputActive && _state.Editor.Text.Length > 0)
            {
                _state.Editor.Clear();
                _state.InputChanged();
                return;
            }
            if (_state.Editing && _state.Current?.Textual != true) { _state.CloseEdit(); return; }
            await Cancel(args.CancellationToken);
            return;
        }
        if (control && key.Key == ConsoleKey.V || shift && key.Key == ConsoleKey.Insert)
        {
            await PasteClipboard(args.CancellationToken);
            return;
        }
        if (key.Key == ConsoleKey.Tab)
        {
            _state.MoveTab(shift ? -1 : 1);
            _reviewOffset = 0;
            return;
        }
        if (_state.InputActive)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                if (shift) { _state.Editor.Paste("\n"); _state.InputChanged(); return; }
                var multiple = _state.Current?.Kind == TerminalFormFieldKind.Multiselect;
                if (!_state.CommitInput() || multiple) return;
                if (_state.Single) await Submit(args.CancellationToken);
                else _state.SelectTab(_state.Tab + 1);
                _reviewOffset = 0;
                return;
            }
            if (_state.Editing && key.Key == ConsoleKey.UpArrow && _state.Selected > 0
                && args.Measure(_state.Editor.Text, ContentWidth).Position(_state.Editor.Cursor).Row == 0)
            {
                _state.CloseEdit();
                _state.MoveOption(-1);
                return;
            }
            _state.Editor.Handle(key, args.Measure(_state.Editor.Text, ContentWidth));
            _state.InputChanged();
            return;
        }
        // The original form intercepts printable input on the custom row before
        // h/j/k/l and numeric choice shortcuts, so typing starts an answer edit.
        if (_state.Other && !control && !key.Modifiers.HasFlag(ConsoleModifiers.Alt) && !char.IsControl(key.KeyChar) && key.KeyChar != ' ')
        {
            _state.BeginCustom();
            _state.Editor.Handle(key, args.Measure(_state.Editor.Text, ContentWidth));
            _state.InputChanged();
            return;
        }
        if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow || !control && key.KeyChar is 'h' or 'l')
        {
            _state.MoveTab(key.Key == ConsoleKey.LeftArrow || key.KeyChar == 'h' ? -1 : 1);
            _reviewOffset = 0;
            return;
        }
        if (_state.Review)
        {
            if (key.Key == ConsoleKey.Enter) await Submit(args.CancellationToken);
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or ConsoleKey.Home or ConsoleKey.End
                || !control && key.KeyChar is 'j' or 'k')
            {
                var delta = key.Key switch
                {
                    ConsoleKey.Home => -int.MaxValue,
                    ConsoleKey.End => int.MaxValue,
                    ConsoleKey.PageUp => -ReviewHeight,
                    ConsoleKey.PageDown => ReviewHeight,
                    ConsoleKey.UpArrow => -1,
                    _ => key.KeyChar == 'k' ? -1 : 1
                };
                _reviewOffset = (int)Math.Clamp((long)_reviewOffset + delta, 0, Math.Max(0, ReviewLines().Count - ReviewHeight));
            }
            return;
        }
        if (_state.Current is { Kind: TerminalFormFieldKind.External })
        {
            if (key.Key == ConsoleKey.Enter) await External(false, args.CancellationToken);
            if (!control && key.KeyChar == 'c') await External(true, args.CancellationToken);
            return;
        }
        if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow || !control && key.KeyChar is 'j' or 'k')
        {
            _state.MoveOption(key.Key == ConsoleKey.UpArrow || key.KeyChar == 'k' ? -1 : 1);
            return;
        }
        if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar && _state.Current?.Kind == TerminalFormFieldKind.Multiselect)
        {
            if (_state.Choose()) await Submit(args.CancellationToken);
            return;
        }
        if (!control && key.KeyChar is >= '1' and <= '9')
        {
            if (_state.Choose(key.KeyChar - '1')) await Submit(args.CancellationToken);
            return;
        }
    }

    public void Paste(TerminalPasteEventArgs args)
    {
        args.Handled = true;
        if (Locked) return;
        PasteText(args.Text);
    }

    private void PasteText(string text)
    {
        if (_state.Review || _state.Current?.Kind == TerminalFormFieldKind.External) return;
        if (!_state.InputActive)
        {
            if (_state.Current?.AllowsCustom != true) return;
            _state.BeginCustom();
        }
        try { _state.Editor.Paste(text); _state.InputChanged(); }
        catch (ArgumentException exception) { _state.SetError(exception.Message); }
    }

    private async Task PasteClipboard(CancellationToken cancellationToken)
    {
        if (ReadClipboard is null) { _state.SetError("Clipboard reading is unavailable."); return; }
        await Run(async token =>
        {
            var text = await ReadClipboard(token);
            token.ThrowIfCancellationRequested();
            if (text is not null) PasteText(text);
        }, null, cancellationToken);
    }

    private Task Submit(CancellationToken cancellationToken)
    {
        if (Locked || !_state.TryAnswer(out var answer)) return Task.CompletedTask;
        if (UnavailableReason is not null || OnReply is null)
        {
            _state.SetError(UnavailableReason ?? "Form replies are not connected to the server.");
            return Task.CompletedTask;
        }
        var request = new FormReplyRequest(Request.Form.SessionId, Request.Form.Id, Request.Location, FormAdapter.Reply(answer));
        return Run(token => OnReply(request, token), "Reply sent. Waiting for server update.", cancellationToken);
    }

    private Task Cancel(CancellationToken cancellationToken)
    {
        if (Locked) return Task.CompletedTask;
        if (UnavailableReason is not null || OnCancel is null)
        {
            _state.SetError(UnavailableReason ?? "Form cancellation is not connected to the server.");
            return Task.CompletedTask;
        }
        var request = new FormCancelRequest(Request.Form.SessionId, Request.Form.Id, Request.Location);
        return Run(token => OnCancel(request, token), "Cancellation sent. Waiting for server update.", cancellationToken);
    }

    private Task External(bool copy, CancellationToken cancellationToken, bool acknowledge = true)
    {
        if (_state.Current is not { Kind: TerminalFormFieldKind.External, Url: { } url } field) return Task.CompletedTask;
        if (!copy && acknowledge && _state.ExternalReady(field.Key)) { _state.AcknowledgeExternal(); return Task.CompletedTask; }
        var action = copy ? CopyExternal : OpenExternal;
        if (action is null)
        {
            _state.SetError(copy ? "Copying links is unavailable." : "Opening links is unavailable. Use c to copy the URL if supported.");
            return Task.CompletedTask;
        }
        return Run(async token =>
        {
            await action(url, token);
            token.ThrowIfCancellationRequested();
            _state.MarkExternalReady(field.Key);
            _notice = copy ? "URL copied. Complete the action, then confirm." : null;
        }, null, cancellationToken);
    }

    private async Task Run(Func<CancellationToken, Task> action, string? completed, CancellationToken cancellationToken)
    {
        if (Locked) return;
        var lifetime = _lifetime;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        _busy = true;
        _notice = null;
        _state.SetError(null);
        StateHasChanged();
        try
        {
            await action(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(lifetime, _lifetime) || _disposed) return;
            if (completed is not null) { _finished = true; _notice = completed; }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (ReferenceEquals(lifetime, _lifetime) && !_disposed)
                _state.SetError("Operation cancelled. Refresh pending forms before retrying.");
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(lifetime, _lifetime) && !_disposed) _state.SetError(exception.Message);
        }
        finally
        {
            if (ReferenceEquals(lifetime, _lifetime) && !_disposed) _busy = false;
        }
    }

    private sealed record ReviewLine(string Text, string Color);

    private IReadOnlyList<ReviewLine> ReviewLines() => _state.Visible.SelectMany(field =>
    {
        var value = _state.Value(field.Key);
        var error = TerminalFormValidation.Validate(field, value);
        var text = $"{Truncate(field.Label, 40)}: " + (error ?? (field.Kind == TerminalFormFieldKind.External ? "Acknowledged"
            : value is null ? "(not answered)" : TerminalFormValidation.Display(field, value)));
        var color = error is not null ? Theme.Error : field.Kind == TerminalFormFieldKind.External ? Theme.Success : value is null ? Theme.Subdued : Theme.Text;
        // Use the host's native-width measurement, then render a bounded, top-origin review viewport.
        // TuiText.Scroll currently only supports tail offsets; do not depend on it for form review.
        var lines = _measure?.Invoke(text, ContentWidth).Lines;
        return lines is null ? [new ReviewLine(text, color)]
            : lines.Select(line => new ReviewLine(text[line.Start..line.End], color)).ToArray();
    }).ToArray();

    private IEnumerable<ReviewLine> VisibleReviewLines()
    {
        var lines = ReviewLines();
        _reviewOffset = Math.Clamp(_reviewOffset, 0, Math.Max(0, lines.Count - ReviewHeight));
        return lines.Skip(_reviewOffset).Take(ReviewHeight);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    public void Dispose()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
