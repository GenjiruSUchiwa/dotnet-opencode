namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.Dialogs;
using OpenCode.Cli.Tui.Permissions;
using OpenCode.Cli.Tui.Tabs;
using OpenCode.Cli.Tui.Theme;
using OpenCode.Cli.Tui.Transcript;
using OpenCode.Cli.Tui.Settings;
using OpenTui.Blazor.Forms;
using OpenTui.Blazor;

public partial class OpenCodeApp
{
    [Parameter] public ThemeState? Themes { get; set; }
    [Parameter] public Action<TerminalRenderColors>? ApplyRenderColors { get; set; }
    private static readonly Lazy<ResolvedTheme> BuiltinTheme = new(() => new ThemeCatalog().Resolve("opencode", ThemeMode.Dark));
    private ResolvedTheme ThemeView => Themes?.Current ?? BuiltinTheme.Value;
    private ResolvedTheme? _previousTheme;
    [Parameter] public Func<CliThemeSettings, CancellationToken, Task>? PersistThemeSettings { get; set; }
    /// <summary>Root-owned store/controller/catalog are captured by this factory. The app owns
    /// the returned subscription adapter, not another configuration writer.</summary>
    [Parameter] public Func<ThemeState, Action<Exception>, ThemeSettingsPersistence>? CreateThemePersistence { get; set; }
    private ThemeState? _connectedThemes;
    private ThemeSettingsPersistence? _themePersistence;
    private readonly Lock _themeUpdateGate = new();
    private bool _themesDisconnected;
    private Task _themeUpdate = Task.CompletedTask;
    private string? _themeError;
    private ThemeTokens BaseColors => ThemeView.Base;
    private ThemeTokens ElevatedColors => ThemeView.Elevated;
    private ThemeTokens OverlayColors => ThemeView.Overlay;
    private WordmarkTheme WordmarkColors => WordmarkTheme.From(BaseColors);

    private string AgentColor
    {
        get
        {
            var agents = (_presentation?.Agents ?? _catalog?.Agents ?? []).Where(agent => !agent.Hidden).ToArray();
            var index = Array.FindIndex(agents, agent => agent.Id == _agentSelection);
            if (index >= 0 && agents[index].Color is { } color) return ThemeColor.Parse(color).Hex;
            var colors = BaseColors.Categorical.Select(scale => scale[ThemeView.Mode == ThemeMode.Light ? 800 : 200]).DistinctBy(color => color.Hex).ToArray();
            return colors[Math.Max(0, index) % colors.Length].Hex;
        }
    }

    private TranscriptTheme TranscriptColors => new(
        BaseColors.Text.Hex, BaseColors.Subdued.Hex, BaseColors.Background.Hex, ElevatedColors.Background.Hex,
        BaseColors.Raise(BaseColors.Background).Hex, BaseColors.Border.Hex, AgentColor,
        BaseColors.Color("text.feedback.warning.default").Hex, BaseColors.Color("text.feedback.error.default").Hex,
        BaseColors.Color("text.feedback.success.default").Hex, BaseColors.Color("markdown.text").Hex,
        BaseColors.Color("markdown.heading").Hex, BaseColors.Color("markdown.code").Hex,
        BaseColors.Color("markdown.link").Hex, BaseColors.Color("markdown.blockQuote").Hex,
        BaseColors.Color("markdown.listItem").Hex, BaseColors.Color("diff.text.added").Hex,
        BaseColors.Color("diff.text.removed").Hex, BaseColors.Color("diff.text.hunkHeader").Hex);

    private PermissionTheme PermissionColors => new(
        ElevatedColors.Text.Hex, ElevatedColors.Subdued.Hex, ElevatedColors.Background.Hex,
        ElevatedColors.Raise(ElevatedColors.Background).Hex,
        ElevatedColors.Color("text.feedback.warning.default").Hex, ElevatedColors.Color("text.feedback.error.default").Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary).Hex, ElevatedColors.ActionBackground(ThemeActionVariant.Primary).Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        ElevatedColors.ActionBackground(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        ElevatedColors.ActionText(ThemeActionVariant.Primary, ThemeActionState.Disabled).Hex);

    private TerminalFormTheme FormColors => new(
        ElevatedColors.Text.Hex, ElevatedColors.Subdued.Hex, ElevatedColors.Background.Hex, ElevatedColors.Border.Hex,
        ElevatedColors.FormfieldText().Hex, ElevatedColors.FormfieldText(ThemeActionState.Focused).Hex,
        ElevatedColors.FormfieldText(ThemeActionState.Selected).Hex, ElevatedColors.FormfieldBackground(ThemeActionState.Focused).Hex,
        ElevatedColors.FormfieldBackground(ThemeActionState.Selected).Hex, ElevatedColors.ActionText(ThemeActionVariant.Primary).Hex,
        ElevatedColors.Color("text.feedback.success.default").Hex, ElevatedColors.Color("text.feedback.error.default").Hex);

    // Both ui/dialog.tsx and ui/dialog-select.tsx use the elevated context. The
    // black alpha-150 scrim is an explicit source primitive, not a borrowed theme token.
    private DialogTheme DialogColors => new(
        Text: ElevatedColors.Text.Hex, Subdued: ElevatedColors.Subdued.Hex, Background: ElevatedColors.Background.Hex,
        Backdrop: ThemeColor.FromInts(0, 0, 0, 150).Hex, Category: ElevatedColors.Text.Hex,
        FocusedBackground: ElevatedColors.ActionBackground(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        FocusedText: ElevatedColors.ActionText(ThemeActionVariant.Primary, ThemeActionState.Focused).Hex,
        SelectedText: ElevatedColors.FormfieldText(ThemeActionState.Selected).Hex,
        InputBackground: ElevatedColors.FormfieldBackground(ThemeActionState.Focused).Hex,
        InputText: ElevatedColors.FormfieldText(ThemeActionState.Focused).Hex);

    private SessionTabsTheme TabColors => new(
        BaseColors.Text.Hex, BaseColors.Subdued.Hex, BaseColors.Background.Hex, ElevatedColors.Background.Hex,
        BaseColors.ActionBackground(ThemeActionVariant.Secondary, ThemeActionState.Hovered).Hex,
        BaseColors.FormfieldText().Hex, BaseColors.Color("text.status.running").Hex,
        BaseColors.Color("text.status.unread").Hex, BaseColors.Color("text.status.permission").Hex,
        BaseColors.Color("text.status.question").Hex, BaseColors.Color("text.feedback.error.default").Hex,
        BaseColors.ActionBackground(ThemeActionVariant.Destructive, ThemeActionState.Focused).Hex,
        BaseColors.ActionText(ThemeActionVariant.Destructive, ThemeActionState.Focused).Hex);

    private void ConnectThemes()
    {
        if (Themes is null) return;
        if (ReferenceEquals(_connectedThemes, Themes)) return;
        if (_connectedThemes is not null) throw new InvalidOperationException("ThemeState and its settings registrations are application-owned; remount to replace them.");
        _themesDisconnected = false;
        _connectedThemes = Themes;
        _connectedThemes.Changed += ThemeChanged;
        _connectedThemes.Error += ThemeFailed;
        if (CreateThemePersistence is not null)
            _themePersistence = CreateThemePersistence(_connectedThemes, ThemeFailed);
        // Compatibility with the original callback parameter. Never subscribe a
        // second persistence path when the shared settings adapter is present.
        else if (PersistThemeSettings is not null)
            _connectedThemes.PersistenceRequested += ThemePersistenceRequested;
    }

    private void ThemeChanged() => QueueThemeUpdate(() => { _themeError = null; return Task.CompletedTask; });
    // Text selection is native per-run inversion, not the selected state of a
    // form field/list option. Upstream text and textarea leave both overrides unset.
    private void ApplyHostTheme() => ApplyRenderColors?.Invoke(new(BaseColors.Text.Native, BaseColors.Background.Native,
        Cursor: BaseColors.Text.Native));
    private void ThemeFailed(Exception exception) => QueueThemeUpdate(() => { _themeError = exception.Message; return Task.CompletedTask; });
    private void ThemePersistenceRequested(CliThemeSettings settings) => QueueThemeUpdate(async () =>
    {
        if (_themePersistence is not null) return;
        if (PersistThemeSettings is null) throw new InvalidOperationException("Theme settings persistence is unavailable.");
        await PersistThemeSettings(settings, _configurationLifetime.Token);
        _themeError = null;
    });

    private void QueueThemeUpdate(Func<Task> apply)
    {
        // Save failures may arrive off-dispatcher. Retain all work, not only the last
        // InvokeAsync task, so the existing StopAsync await drains the complete chain.
        lock (_themeUpdateGate)
        {
            if (_themesDisconnected) return;
            _themeUpdate = DispatchThemeUpdate(_themeUpdate, apply);
        }
    }

    private async Task DispatchThemeUpdate(Task previous, Func<Task> apply)
    {
        await previous.ConfigureAwait(false);
        await InvokeAsync(async () =>
        {
            if (_themesDisconnected) return;
            try { await apply(); }
            catch (OperationCanceledException) when (_configurationLifetime.IsCancellationRequested) { }
            catch (Exception exception) { _themeError = exception.Message; }
            // OnFrame owns StateHasChanged and combines theme changes with other UI updates.
            _dirty = true;
        });
    }

    private void DisconnectThemes()
    {
        lock (_themeUpdateGate)
        {
            if (_themesDisconnected) return;
            _themesDisconnected = true;
            if (_connectedThemes is not null)
            {
                _connectedThemes.Changed -= ThemeChanged;
                _connectedThemes.Error -= ThemeFailed;
                _connectedThemes.PersistenceRequested -= ThemePersistenceRequested;
                _connectedThemes = null;
            }
            if (_themePersistence is null) return;
            // Dispose detaches its Save listener immediately, then awaits its shared
            // writer queue. StopAsync already awaits _themeUpdate after disconnecting.
            _themeUpdate = Task.WhenAll(_themeUpdate, _themePersistence.DisposeAsync().AsTask());
            _themePersistence = null;
        }
    }
}
