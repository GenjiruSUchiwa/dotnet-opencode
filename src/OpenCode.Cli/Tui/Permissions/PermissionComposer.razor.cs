namespace OpenCode.Cli.Tui.Permissions;

using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using OpenCode.Schema;
using OpenTui.Blazor;
using OpenCode.Cli.Tui.ToolViews;
using OpenCode.Cli.Tui.Transcript;

public partial class PermissionComposer : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public PermissionRequest Request { get; set; } = null!;
    [Parameter, EditorRequired] public PermissionTheme Theme { get; set; } = null!;
    [Parameter, EditorRequired] public Func<PermissionDecision, CancellationToken, Task>? OnReply { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }
    [Parameter] public bool CanPersistAlways { get; set; }
    // Upstream asks for feedback for child sessions. The owner supplies that session fact.
    [Parameter] public bool RequestRejectionFeedback { get; set; }
    [Parameter] public IReadOnlyDictionary<string, JsonElement>? SourceInput { get; set; }
    [Parameter] public IReadOnlyDictionary<string, JsonElement>? SourceMetadata { get; set; }
    [Parameter] public string? SourceError { get; set; }
    [Parameter] public TranscriptTheme? DiffTheme { get; set; }
    [Parameter] public ToolViewBindings? DiffBindings { get; set; }
    [Parameter] public int Width { get; set; } = 75;
    [Parameter] public int TerminalWidth { get; set; } = 80;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public string FocusKey { get; set; } = "permission";

    private enum Stage { Permission, Always, Reject }
    private Stage _stage;
    private PermissionId? _requestId;
    private SessionId? _sessionId;
    private CancellationTokenSource? _pending;
    private bool _submitted;
    private bool _disposed;
    private int _selected;
    private int _scroll;
    private string _feedback = "";
    private int _cursor;
    private char? _surrogate;
    private string? _error;
    private readonly TerminalScrollState _editScroll = new() { AutoFollow = false };
    private bool RichEdit => _stage == Stage.Permission && Action == "edit" && DiffTheme is not null;
    private int EditHeight => Math.Max(1, Math.Min(15, TerminalHeight) - (TerminalWidth < 80 ? 9 : 7)
        - (Width < 44 ? 4 : 0) - 1 - (_error is null ? 0 : 2) - (SourceError is null ? 0 : 1));
    private string? EditDiff
    {
        get
        {
            var files = Metadata("files");
            var first = files is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0 ? array[0] : default;
            var patch = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("patch", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!string.IsNullOrEmpty(patch)) return patch;
            var diff = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("diff", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return !string.IsNullOrEmpty(diff) ? diff : MetadataText("diff");
        }
    }
    private bool AlwaysEnabled => CanPersistAlways && Request.Save is { Count: > 0 };
    private string[] Labels => _stage == Stage.Always ? ["Confirm", "Cancel"] : ["Allow once", "Always allow", "Reject"];
    private string Title => _stage switch { Stage.Always => "Always allow", Stage.Reject => "Reject permission", _ => "Permission required" };
    private string Hint => _pending is not null ? "Sending...  esc cancel" : _submitted ? "Reply sent"
        : _stage == Stage.Reject ? "Rejection reason" : "left/right select  enter confirm";
    private int BodyHeight => Math.Max(1, Math.Min(Body.Split('\n').Sum(line => Math.Max(1, (line.Length + Math.Max(1, Width - 6) - 1) / Math.Max(1, Width - 6))),
        Math.Min(15, TerminalHeight) - (TerminalWidth < 80 ? 9 : 7) - (Width < 44 ? 4 : 0)
        - (_stage == Stage.Permission && Action != "shell" ? 1 : 0) - (_error is null ? 0 : 2)));
    private string Action => Request.Action switch { "bash" => "shell", "task" => "subagent", "apply_patch" => "patch", _ => Request.Action };
    private string ActionIcon => Action switch
    {
        "edit" or "read" or "list" or "lsp" => "\u2192",
        "glob" or "grep" => "\u2731",
        "shell" or "subagent" => "#",
        "webfetch" => "%",
        "websearch" => "\u25c8",
        "external_directory" => "\u2190",
        "doom_loop" => "\u27f3",
        _ => "\u2699"
    };
    private string Path => InputText("path") ?? InputText("filePath") ?? InputText("filepath") ?? Request.Resources.FirstOrDefault() ?? "";
    private string Pattern => InputText("pattern") ?? Request.Resources.FirstOrDefault() ?? "";
    private string ActionTitle => Action switch
    {
        "edit" => $"Edit {Path}",
        "read" => $"Read {Path}",
        "list" => $"List {Path}",
        "glob" => $"Glob \"{Pattern}\"",
        "grep" => $"Grep \"{Pattern}\"",
        "subagent" => $"{InputText("agent") ?? InputText("subagent_type") ?? "general"} Subagent",
        "webfetch" => $"WebFetch {InputText("url") ?? MetadataText("url")}",
        "websearch" => $"Web Search{(MetadataText("provider") is { } provider ? $" via {provider}" : "")} \"{InputText("query") ?? MetadataText("query")}\"",
        "lsp" => $"LSP {InputText("operation") ?? "request"} {Path}",
        "external_directory" => $"Access external directory {MetadataText("parentDir") ?? MetadataText("filepath") ?? Path}",
        "doom_loop" => "Continue after repeated failures",
        _ => $"Call tool {Request.Action}"
    };

    private string Body
    {
        get
        {
            if (_stage == Stage.Reject) return "Tell OpenCode what to do differently";
            if (_stage == Stage.Always)
                return Request.Save is { Count: 1 } save && save[0] == "*"
                    ? $"This will always allow {Request.Action} for this project."
                    : "This will always allow the following patterns for this project.\n" + string.Join('\n', Request.Save!.Select(value => $"- {value}"));

            var lines = new List<string>();
            if (Action == "shell" && InputText("command") is { } command) lines.Add($"$ {command}");
            if (Action is "read" or "list") lines.Add($"Path: {Path}");
            if (Action is "glob" or "grep") lines.Add($"Pattern: {Pattern}");
            if (Action == "subagent" && InputText("description") is { } description) lines.Add(description);
            if (Action == "external_directory") lines.Add("Patterns");
            if (Action == "doom_loop") lines.Add("This keeps the session running despite repeated failures.");
            if (Action == "edit")
            {
                lines.Add(EditDiff ?? InputText("patchText") ?? "No diff provided");
            }
            if (!string.IsNullOrEmpty(Request.Message)) lines.Add(Request.Message);
            lines.AddRange(Request.Resources.Select(value => $"- {value}"));
            if (Request.Source is { } source)
                lines.Add($"Source: {source.Type} {source.Id} (message {source.MessageId})");
            if (SourceInput is not null)
                lines.AddRange(SourceInput.Select(item => $"Input {item.Key}: {Display(item.Value)}"));
            if (SourceMetadata is not null)
                lines.AddRange(SourceMetadata.Where(item => Request.Metadata?.ContainsKey(item.Key) != true)
                    .Select(item => $"{item.Key}: {Display(item.Value)}"));
            if (Request.Metadata is not null)
                lines.AddRange(Request.Metadata.Select(item => $"{item.Key}: {Display(item.Value)}"));
            if (!AlwaysEnabled) lines.Add("Always allow unavailable: " + (CanPersistAlways ? "no save patterns." : "persistence support has not been confirmed."));
            return string.Join('\n', lines);
        }
    }

    private async Task ConfirmSelection(CancellationToken cancellationToken)
    {
        if (_disposed || _submitted || _pending is not null) return;
        if (_stage == Stage.Reject) { await Send(PermissionReply.Reject, cancellationToken); return; }
        if (_stage == Stage.Always)
        {
            if (_selected == 0 && AlwaysEnabled) await Send(PermissionReply.Always, cancellationToken);
            else { _stage = Stage.Permission; _selected = _scroll = 0; }
            return;
        }
        if (_selected == 0) { await Send(PermissionReply.Once, cancellationToken); return; }
        if (_selected == 1 && AlwaysEnabled) { _stage = Stage.Always; _selected = _scroll = 0; return; }
        if (_selected == 2) await Reject(cancellationToken);
    }

    private async Task ChooseAction(int index, TerminalPointerEventArgs args)
    {
        args.Handled = true;
        if (_disposed || _submitted || _pending is not null || _stage == Stage.Reject || index < 0 || index >= Labels.Length
            || _stage == Stage.Permission && index == 1 && !AlwaysEnabled) return;
        _selected = index;
        await ConfirmSelection(CancellationToken.None);
    }

    private static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    private string? InputText(string key) => SourceInput?.TryGetValue(key, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private JsonElement? Metadata(string key) => Request.Metadata?.TryGetValue(key, out var value) == true ? value
        : SourceMetadata?.TryGetValue(key, out value) == true ? value : null;
    private string? MetadataText(string key) => Metadata(key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    protected override void OnParametersSet()
    {
        if (_requestId == Request.Id && _sessionId == Request.SessionId)
        {
            if (_stage == Stage.Always && !AlwaysEnabled) _stage = Stage.Permission;
            if (_stage == Stage.Permission && _selected == 1 && !AlwaysEnabled) _selected = 0;
            return;
        }
        _pending?.Cancel();
        _pending = null;
        _requestId = Request.Id;
        _editScroll.ScrollToStart();
        _sessionId = Request.SessionId;
        _stage = Stage.Permission;
        _selected = _scroll = _cursor = 0;
        _feedback = "";
        _error = null;
        _surrogate = null;
        _submitted = false;
    }

    public async Task Key(TerminalKeyEventArgs args)
    {
        args.Handled = true;
        var key = args.Key;
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (_pending is not null)
        {
#pragma warning disable MA0042 // A key event cancels the pending submission before returning; preserve callback ordering on the dispatcher.
            if (key.Key == ConsoleKey.Escape || control && key.Key == ConsoleKey.C) _pending.Cancel();
#pragma warning restore MA0042
            return;
        }
        if (_submitted || _disposed) return;
        if (key.Key == ConsoleKey.Escape || control && key.Key == ConsoleKey.C)
        {
            if (_stage == Stage.Reject && control && _feedback.Length > 0)
            {
                _feedback = "";
                _cursor = 0;
                return;
            }
            if (_stage != Stage.Permission)
            {
                _stage = Stage.Permission;
                _selected = _scroll = 0;
                return;
            }
            await Reject(args.CancellationToken);
            return;
        }
        if (key.Key == ConsoleKey.Enter)
        {
            await ConfirmSelection(args.CancellationToken);
            return;
        }
        if (_stage != Stage.Reject)
        {
            if (key.Key is ConsoleKey.PageUp or ConsoleKey.PageDown)
            {
                if (RichEdit) _editScroll.ScrollBy(key.Key == ConsoleKey.PageUp ? -EditHeight : EditHeight);
                else _scroll = Math.Clamp(_scroll + (key.Key == ConsoleKey.PageUp ? -BodyHeight : BodyHeight), 0,
                    Math.Max(0, args.Measure(Body, Math.Max(1, Width - 6)).Lines.Count - BodyHeight));
            }
            if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow || !control && key.KeyChar is 'h' or 'l')
            {
                var direction = key.Key == ConsoleKey.LeftArrow || key.KeyChar == 'h' ? -1 : 1;
                do { _selected = (_selected + direction + Labels.Length) % Labels.Length; }
                while (_stage == Stage.Permission && _selected == 1 && !AlwaysEnabled);
            }
            return;
        }
        var boundaries = StringInfo.ParseCombiningCharacters(_feedback);
        var previous = boundaries.LastOrDefault(index => index < _cursor);
        var next = boundaries.FirstOrDefault(index => index > _cursor, _feedback.Length);
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow: _cursor = previous; break;
            case ConsoleKey.RightArrow: _cursor = next; break;
            case ConsoleKey.Home: _cursor = 0; break;
            case ConsoleKey.End: _cursor = _feedback.Length; break;
            case ConsoleKey.Backspace when _cursor > 0:
                _feedback = _feedback.Remove(previous, _cursor - previous); _cursor = previous; break;
            case ConsoleKey.Delete when _cursor < _feedback.Length:
                _feedback = _feedback.Remove(_cursor, next - _cursor); break;
            default:
                if (control || key.Modifiers.HasFlag(ConsoleModifiers.Alt) || char.IsControl(key.KeyChar)) break;
                if (char.IsHighSurrogate(key.KeyChar)) { _surrogate = key.KeyChar; break; }
                var text = char.IsLowSurrogate(key.KeyChar) ? _surrogate is char high ? new string([high, key.KeyChar]) : "" : key.KeyChar.ToString();
                _surrogate = null;
                _feedback = _feedback.Insert(_cursor, text);
                _cursor += text.Length;
                break;
        }
    }

    public void Paste(TerminalPasteEventArgs args)
    {
        args.Handled = true;
        if (_stage != Stage.Reject || _pending is not null || _submitted || _disposed) return;
        var text = string.Concat(args.Text.Where(value => !char.IsControl(value) || value == '\n'));
        _feedback = _feedback.Insert(_cursor, text);
        _cursor += text.Length;
        _surrogate = null;
    }

    private async Task Reject(CancellationToken cancellationToken)
    {
        if (RequestRejectionFeedback) { _stage = Stage.Reject; _scroll = 0; return; }
        await Send(PermissionReply.Reject, cancellationToken);
    }

    private async Task Send(PermissionReply reply, CancellationToken cancellationToken)
    {
        if (_pending is not null || _submitted || reply == PermissionReply.Always && !AlwaysEnabled) return;
        if (OnReply is null) { _error = "Permission reply handler is unavailable."; return; }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pending = pending;
        _error = null;
        var decision = new PermissionDecision(Request.SessionId, Request.Id, reply,
            reply == PermissionReply.Reject && !string.IsNullOrWhiteSpace(_feedback) ? _feedback : null);
        try
        {
            await OnReply(decision, pending.Token);
            if (ReferenceEquals(_pending, pending) && !_disposed) _submitted = true;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            if (ReferenceEquals(_pending, pending) && !_disposed)
            {
                _error = "Reply cancelled. Check the pending request before retrying.";
                await OnCancel.InvokeAsync();
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_pending, pending) && !_disposed) _error = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _editScroll.Detach();
        _pending?.Cancel();
    }
}
