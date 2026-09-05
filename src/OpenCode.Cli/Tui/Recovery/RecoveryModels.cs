namespace OpenCode.Cli.Tui.Recovery;

using System.Net;
using OpenCode.Client;
using OpenCode.Protocol.Errors;
using OpenCode.Schema;

/// <summary>Only server-declared Location unavailability. This does not assert that a directory is missing.</summary>
public sealed record LocationRecoveryEvidence
{
    public SessionId SessionId { get; }
    public LocationRef Location { get; }
    public string Operation { get; }
    public ServiceUnavailableError Error { get; }

    private LocationRecoveryEvidence(SessionId session, LocationRef location, string operation, ServiceUnavailableError error)
    { SessionId = session; Location = location; Operation = operation; Error = error; }

    public static LocationRecoveryEvidence? From(SessionId session, LocationRef? location, Exception error) =>
        location is not null && error is SessionApiException
        {
            StatusCode: HttpStatusCode.ServiceUnavailable,
            QueryError: ServiceUnavailableError { Service: "location" } unavailable
        } response ? new(session, location, response.Operation, unavailable) : null;
}

public sealed record RecoveryTheme(string Text, string Subdued, string Background, string RaisedBackground,
    string Warning, string Error, string ActionBackground, string FocusedActionBackground,
    string ActionText, string FocusedActionText, string DisabledText);

public static class RecoveryPresentation
{
    public static int OverlayDelay(SessionFeedPhase phase) => phase == SessionFeedPhase.Reconnecting ? 1000 : 5000;
    public static int OverlayWidth(int terminalWidth) => Math.Max(1, Math.Min(48, (int)Math.Floor(Math.Max(1, terminalWidth) * .9)));

    public static string DirectoryLabel(string directory, string home)
    {
        var value = directory;
        if (home.Length > 0)
        {
            var root = home.Replace('\\', '/').TrimEnd('/');
            var path = directory.Replace('\\', '/');
            var comparison = home.Length > 1 && home[1] == ':' || home.StartsWith("\\\\", StringComparison.Ordinal)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(path.TrimEnd('/'), root, comparison)) value = "~";
            else if (path.StartsWith(root + "/", comparison)) value = "~/" + path[(root.Length + 1)..];
        }
        return value.Length <= 72 ? value : value[..36] + "…" + value[^35..];
    }
}
