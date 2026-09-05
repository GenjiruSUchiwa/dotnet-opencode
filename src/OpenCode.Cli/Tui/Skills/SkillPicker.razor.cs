namespace OpenCode.Cli.Tui.Skills;

using Microsoft.AspNetCore.Components;
using OpenCode.Client;
using OpenCode.Schema;
using OpenCode.Cli.Tui.Dialogs;

/// <summary>Source-shaped large skill picker. Root owns prompt text/mention positions and subsequent admission.</summary>
public partial class SkillPicker : ComponentBase, IDisposable
{
    [Parameter, EditorRequired] public SessionHttpClient Client { get; set; } = null!;
    [Parameter, EditorRequired] public LocationRef Location { get; set; } = null!;
    [Parameter, EditorRequired] public DialogTheme Theme { get; set; } = null!;
    [Parameter, EditorRequired] public string ErrorColor { get; set; } = null!;
    [Parameter] public int TerminalHeight { get; set; } = 24;
    [Parameter] public SkillCatalogSnapshot? Catalog { get; set; }
    [Parameter] public string? UnavailableReason { get; set; }
    [Parameter] public Func<ConsoleKeyInfo, string?>? ResolveCommand { get; set; }
    [Parameter] public EventCallback<SkillSelection> OnSelect { get; set; }
    [Parameter] public EventCallback<SkillInfo> OnFocus { get; set; }
    [Parameter] public EventCallback<SkillCatalogSnapshot> OnLoaded { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private readonly CancellationTokenSource _lifetime = new();
    private SessionHttpClient _client = null!;
    private LocationRef _location = null!;
    private SkillCatalogSnapshot? _catalog;
    private bool _loading = true;
    private bool _disposed;
    private string? _error;
    private string? _selectionError;

    protected override async Task OnInitializedAsync()
    {
        _client = Client;
        _location = Location;
        if (UnavailableReason is not null) { _error = UnavailableReason; _loading = false; return; }
        if (Catalog is not null)
        {
            if (Catalog.Location != Location) { _error = "The cached skill catalog belongs to a different Location."; _loading = false; return; }
            _catalog = Catalog;
            _loading = false;
            return;
        }
        await LoadAsync();
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_client, Client) || _location != Location)
            throw new InvalidOperationException("Remount SkillPicker when its Client or Location changes.");
        if (UnavailableReason is not null) _error = UnavailableReason;
        if (Catalog is { } catalog && catalog.Location == _location) _catalog = catalog;
    }

    public Task RefreshAsync() => InvokeAsync(LoadAsync);
    private async Task LoadAsync()
    {
        if (_disposed) return;
        _loading = true;
        _error = null;
        try
        {
            var result = await _client.ListSkillsAsync(_location.Directory, _location.WorkspaceId?.Value, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            _catalog = new SkillCatalogSnapshot(result);
            await OnLoaded.InvokeAsync(_catalog);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            _catalog = null;
            _error = error is SessionApiException { QueryError: { } detail } ? detail.Message : error.Message;
        }
        finally { _loading = false; }
    }

    private IReadOnlyList<DialogSelectOption<SkillId?>> Options
    {
        get
        {
            if (_catalog is null || _error is not null) return [];
            var width = _catalog.Skills.Select(skill => skill.Name.Length).DefaultIfEmpty().Max();
            return _catalog.Skills.Select(skill => new DialogSelectOption<SkillId?>(skill.Id, skill.Name.PadRight(width),
                SkillCatalogSnapshot.Description(skill), SearchText: skill.Id.Value + " " + SkillCatalogSnapshot.Description(skill))).ToArray();
        }
    }

    private async Task SelectedAsync(SkillId? value)
    {
        if (value is not { } id) return;
        if (_loading || _disposed || _error is not null || _catalog is null) return;
        if (!OnSelect.HasDelegate) { _selectionError = "Skill selection is not connected to the prompt editor."; return; }
        var selection = _catalog.Select(id);
        await OnSelect.InvokeAsync(selection);
        await CloseAsync();
    }

    private void Focused(SkillId? value)
    {
        if (value is not { } id) return;
        if (_catalog is null || !OnFocus.HasDelegate) return;
        _ = NotifyFocusAsync(_catalog.Select(id).Skill);
    }
    private async Task NotifyFocusAsync(SkillInfo skill)
    {
        try { await OnFocus.InvokeAsync(skill); }
        catch (Exception error) { if (!_disposed) await InvokeAsync(() => { _selectionError = error.Message; StateHasChanged(); }); }
    }
    private async Task CloseAsync() { await _lifetime.CancelAsync(); await OnClose.InvokeAsync(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _catalog = null; }
}
