namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Settings;
using OpenCode.Cli.Tui.ToolViews;
using OpenTui.Native;

public partial class OpenCodeApp
{
    [Parameter] public CliSettingsController? Settings { get; set; }
    [Parameter] public Func<string?>? ReadSettingsPersistenceError { get; set; }
    private bool _settings;
    private string? _selectedSetting;
    private string? _settingsError;
    private string? _settingsPersistenceError;
    private bool _configuredThinking;
    private bool _groupExploration = true;
    private bool _sessionImagePreview;
    private Task _settingsUpdate = Task.CompletedTask;

    private void ConnectSettings()
    {
        if (Settings is null || _keyDispatcher is null) return;
        SettingsRuntimeBindings.RegisterKeymap(Settings, _keyDispatcher, () => { _keyHint = null; _dirty = true; });
        Settings.Register(CliSettings.Thinking, value => { _configuredThinking = ShowReasoning = value == "show"; _dirty = true; });
        Settings.Register(CliSettings.Markdown, value => { _markdownMode = value == "source"
            ? OpenCode.Cli.Tui.Transcript.MarkdownPresentation.Source : OpenCode.Cli.Tui.Transcript.MarkdownPresentation.Rendered; _dirty = true; });
        Settings.Register(CliSettings.Sidebar, value => { _frame.SetSidebarPreference(value == "hide"); _dirty = true; });
        Settings.Register(CliSettings.Grouping, value => { _groupExploration = value == "auto"; _dirty = true; });
        Settings.Register(CliSettings.SessionImagePreview, value => { _sessionImagePreview = value; _dirty = true; });
        Settings.Register(CliSettings.DiffView, value =>
        {
            _toolDiffView = value switch { "auto" => ToolDiffView.Auto, "split" => ToolDiffView.Split, "unified" => ToolDiffView.Unified,
                _ => throw new InvalidOperationException("Unsupported diff layout.") };
            _dirty = true;
        });
        Settings.Register(CliSettings.DiffWrap, value =>
        {
            _toolDiffWrap = value switch { "none" => NativeTextWrapMode.None, "word" => NativeTextWrapMode.Word,
                _ => throw new InvalidOperationException("Unsupported diff wrapping.") };
            _dirty = true;
        });
        Settings.Changed += SettingsChanged;
    }

    protected override async Task OnInitializedAsync()
    {
        if (Settings is null) return;
        try { await Settings.LoadAsync(_configurationLifetime.Token); }
        catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
        catch (Exception exception) { _settingsError = exception.Message; }
        _dirty = true;
    }

    private void SettingsChanged() => _settingsUpdate = InvokeAsync(() => { _settingsError = Settings?.Error; _dirty = true; });

    private Task OpenSettings(string? id = null)
    {
        if (Settings is null) return Task.CompletedTask;
        CloseDialog();
        _selectedSetting = id;
        _settings = true;
        _dirty = true;
        return InvokeAsync(StateHasChanged);
    }

    private bool ToggleThinking()
    {
        if (Settings is null) { ShowReasoning = !ShowReasoning; _dirty = true; return true; }
        _keyTasks.Add(Settings.ChangeAsync("session.thinking", 1, _configurationLifetime.Token));
        return true;
    }
}
