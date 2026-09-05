namespace OpenCode.Cli.Tui.Settings;

using OpenTui.Blazor;
using OpenTui.Blazor.Keymap;

public static class SettingsRuntimeBindings
{
    public static void RegisterInteraction(CliSettingsController settings, TerminalInteractionOptions interaction)
    {
        settings.Register(CliSettings.Mouse, value => interaction.MouseEnabled = value);
        settings.Register(CliSettings.ScrollSpeed, value => interaction.ScrollSpeed = value);
    }

    public static void RegisterKeymap(CliSettingsController settings, KeymapDispatcher dispatcher, Action? clearLeaderHint = null)
    {
        settings.Register(CliSettings.LeaderTimeout, value =>
        {
            dispatcher.SetLeaderTimeout(TimeSpan.FromMilliseconds(value));
            clearLeaderHint?.Invoke();
        });
    }
}
