namespace OpenCode.Cli.Tui.Permissions;

using OpenCode.Schema;

public sealed record PermissionDecision(
    SessionId SessionId, PermissionId RequestId, PermissionReply Reply, string? Feedback);

// Colors are resolved by the host theme, not borrowed from outcome colors for actions.
public sealed record PermissionTheme(
    string Text, string Subdued, string ElevatedBackground, string RaisedBackground,
    string Warning, string Error, string ActionText, string ActionBackground,
    string FocusedActionText, string FocusedActionBackground, string DisabledText);
