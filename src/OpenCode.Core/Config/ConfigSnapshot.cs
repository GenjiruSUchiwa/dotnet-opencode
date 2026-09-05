namespace OpenCode.Core.Config;

using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Core-owned observed inputs, not a second wire contract. Document values are
/// retained before projection so canonical serialization cannot discard loader inputs.</summary>
public abstract record ConfigSource
{
    public sealed record Document(string? Path, JsonObject Info) : ConfigSource;
    public sealed record Discovery(ConfigPathEntry Entry) : ConfigSource;
}

public sealed record ConfigDiagnostic(string? Source, string Path, string Kind, string Message);

public sealed record ConfigSnapshot(IReadOnlyList<ConfigSource> Sources, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    /// <summary>Caller-owned merged document using the existing loader's unchanged merge rules.</summary>
    public JsonObject Merge() => ConfigLoader.MergeSources(Sources.OfType<ConfigSource.Document>());

    /// <summary>Canonical Config.Entry[] in source precedence order, never a merged synthetic document.</summary>
    public IReadOnlyList<ConfigEntry> Entries(Action<ConfigDiagnostic>? diagnostic = null)
    {
        foreach (var item in Diagnostics) diagnostic?.Invoke(item);
        return Sources.Select(source => source switch
        {
            ConfigSource.Discovery discovery => (ConfigEntry)discovery.Entry,
            ConfigSource.Document document => new ConfigDocument(ConfigEntryProjection.Normalize(document.Info,
                (path, kind, message) => diagnostic?.Invoke(new(document.Path, path, kind, message))), document.Path),
            _ => throw new InvalidOperationException("Unknown configuration source.")
        }).ToArray();
    }
}
