namespace OpenCode.Core.Instructions;

using OpenCode.Core.Config;
using OpenCode.Core.Reference;
using OpenCode.Core.Skill;
using OpenCode.Schema;

/// <summary>Read-only local source catalogs. Hosts resolve implicit-local placement before calling.</summary>
public static class InstructionCatalog
{
    /// <summary>Uses the same ordered MCP configuration composition as Session execution.</summary>
    public static McpConfiguration ReadMcpConfiguration(string directory) =>
        McpInstructionSource.Configuration(Path.GetFullPath(directory), ConfigLoader.LoadDocument(directory: directory));

    public static async Task<IReadOnlyList<SkillInfo>> ListSkillsAsync(string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var observations = new InstructionLocationState(Path.GetFullPath(directory));
        var source = Sources(directory, observations);
        if (!source.Configuration.SkillsAvailable) throw new IOException("Skill source discovery is temporarily unavailable.");
        var observation = await SkillSources.ReadAsync(source.Configuration.SkillRoots, source.Configuration.Skills(),
            source.Directory, source.Home, ct).ConfigureAwait(false);
        if (!observation.Available) throw new IOException("The complete local skill catalog could not be read.");
        // Skill.list is not agent-filtered; SkillGuidance performs visibility filtering separately.
        return observation.Skills;
    }

    public static Task<IReadOnlyList<ReferenceInfo>> ListReferencesAsync(string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var observations = new InstructionLocationState(Path.GetFullPath(directory));
        var source = Sources(directory, observations);
        if (!source.Configuration.ReferencesAvailable) throw new IOException("Reference source discovery is temporarily unavailable.");
        return Task.FromResult(ReferenceSources.Observe(source.Configuration.Documents, source.Directory, source.Home));
    }

    private static (string Directory, string Home, ProducerConfiguration Configuration) Sources(string directory, InstructionLocationState observations)
    {
        var location = Path.GetFullPath(directory);
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        ProducerConfiguration source;
        try
        {
            source = ProducerConfiguration.Read(location, home, Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory()),
                ConfigLoader.LoadDocument(directory: location), observations);
        }
        catch (InstructionInitializationBlockedException error)
        {
            throw new IOException("No complete local producer configuration is available.", error);
        }
        source.RequireNoPluginSources();
        return (location, home, source);
    }
}
