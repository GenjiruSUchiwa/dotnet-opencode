namespace OpenCode.Cli.Tui;

using OpenCode.Schema;

public sealed record AppCatalog(IReadOnlyList<ModelInfo> Models, IReadOnlyList<ProviderInfo> Providers,
    IReadOnlyList<AgentInfo> Agents, ModelInfo? DefaultModel,
    IReadOnlyList<IntegrationInfo>? Integrations = null, string? IntegrationError = null)
{
    // Catalog availability and implemented native transport families are distinct.
    // Keep metadata visible without presenting another provider SDK as runnable.
    public static bool SupportsTransport(ModelInfo model, ProviderInfo? provider) => (model.Package ?? provider?.Package) is
        "@opencode-ai/ai/providers/google" or "aisdk:@ai-sdk/google"
        or "@opencode-ai/ai/providers/openai-compatible" or "aisdk:@ai-sdk/openai-compatible";
}
