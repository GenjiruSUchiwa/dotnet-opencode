namespace OpenCode.Cli.Tui.Integrations;

using OpenCode.Schema;
using OpenCode.Cli.Tui.Dialogs;

internal static class IntegrationPresentation
{
    public static bool IsMcp(IntegrationInfo integration) => integration.Metadata?.TryGetValue("source", out var source) == true
        && source.ValueKind == System.Text.Json.JsonValueKind.String && source.GetString() == "mcp";

    public static IEnumerable<IntegrationInfo> Ordered(IEnumerable<IntegrationInfo> values) => values
        .OrderByDescending(IsMcp).ThenBy(item => Priority(item.Id.Value)).ThenBy(item => item.Name, StringComparer.CurrentCulture)
        .ThenBy(item => item.Id.Value, StringComparer.Ordinal);

    public static int Priority(string id) => id switch
    { "opencode" => 0, "opencode-go" => 1, "openai" => 2, "github-copilot" => 3, "anthropic" => 4, "google" => 5, _ => 99 };

    // Command authentication is not implemented by this host/UI. Never pretend an environment
    // connection is an account that supports activate/rename/remove.
    public static IntegrationMethod[] Methods(IntegrationInfo integration) => integration.Methods
        .Where(method => method is IntegrationOAuthMethod or IntegrationKeyMethod).OrderBy(method => method is IntegrationKeyMethod ? 1 : 0).ToArray();

    public static ConnectionCredentialInfo[] Credentials(IntegrationInfo integration) => integration.Connections.OfType<ConnectionCredentialInfo>().ToArray();
    public static string Summary(IntegrationInfo integration) => string.Join(", ", integration.Connections.Select(connection => connection switch
    { ConnectionCredentialInfo saved => saved.Label, ConnectionEnvInfo env => "$" + env.Name, _ => "" }));

    public static string? FirstFiltered(IReadOnlyList<DialogSelectOption<string>> options, string query) => options
        .Select((option, index) => (Option: option, Index: index, Score: DialogSearch.Score(query, option.Title, option.Category, option.SearchText)))
        .Where(item => !item.Option.Disabled && item.Score > 0).OrderByDescending(item => item.Score).ThenBy(item => item.Index)
        .FirstOrDefault().Option?.Value;
}
