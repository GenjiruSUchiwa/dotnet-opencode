namespace OpenCode.Cli.Tui.Sessions;

using OpenCode.Cli.Tui.Tabs;

public sealed class SessionPickerStorage
{
    public async Task<bool> LoadAsync(bool allProjectsDefault, CancellationToken ct = default) =>
        (await SessionUiStorage.ReadAsync("session-list", ct))["allProjects"]?.GetValue<bool>() ?? allProjectsDefault;

    public Task SaveAsync(bool allProjects, CancellationToken ct = default, TimeProvider? clock = null) =>
        SessionUiStorage.MutateAsync("session-list", document => document["allProjects"] = allProjects, ct, clock ?? TimeProvider.System);
}
