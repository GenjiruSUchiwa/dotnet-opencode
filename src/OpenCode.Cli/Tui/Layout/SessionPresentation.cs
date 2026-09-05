namespace OpenCode.Cli.Tui.Layout;

using OpenCode.Schema;
using OpenCode.Cli.Tui.Skills;

public sealed record SessionPresentation(SessionInfo? Session, LocationRef Location, IReadOnlyList<ModelInfo> Models,
    IReadOnlyList<McpServer> Mcp, string? McpError = null, IReadOnlyList<AgentInfo>? Agents = null,
    IReadOnlyList<ProviderInfo>? Providers = null, IReadOnlyList<CommandInfo>? Commands = null, string? CommandError = null,
    SkillCatalogSnapshot? Skills = null, string? SkillError = null);
