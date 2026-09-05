namespace OpenCode.Cli.Tui;

using OpenCode.Core.Llm;
using OpenCode.Schema;

public sealed record AppCatalog(IReadOnlyList<ModelInfo> Models, IReadOnlyList<ProviderInfo> Providers,
    IReadOnlyList<AgentInfo> Agents, ModelInfo? DefaultModel,
    IReadOnlyList<IntegrationInfo>? Integrations = null, string? IntegrationError = null)
{
    // Use the backend's exact package capability check. A separate UI allow-list
    // had rejected implemented Anthropic and OpenAI Responses/Chat routes before submission.
    public static bool SupportsTransport(ModelInfo model, ProviderInfo? provider) =>
        ProviderResolver.SupportsPackage(model.Package ?? provider?.Package);
}
