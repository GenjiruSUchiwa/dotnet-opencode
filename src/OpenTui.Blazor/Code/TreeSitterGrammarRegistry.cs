namespace OpenTui.Blazor.Code;

using System.Collections.Immutable;

/// <summary>An explicitly authorized asset. SHA-256 pins bytes even when upstream uses a floating query URL.</summary>
public sealed record TreeSitterGrammarAsset(Uri Source, string Sha256, string Provenance, string License);
public sealed record TreeSitterGrammarRegistration(string Filetype, string LanguageExport,
    TreeSitterGrammarAsset Wasm, IReadOnlyList<TreeSitterGrammarAsset> Highlights,
    IReadOnlyList<string>? Aliases = null, IReadOnlyList<TreeSitterGrammarAsset>? Injections = null,
    IReadOnlyDictionary<string, string>? InjectionNodeTypes = null,
    IReadOnlyDictionary<string, string>? InjectionInfoStrings = null);

/// <summary>Source-shaped explicit registration; registering metadata does not mean a parser is loaded.</summary>
public sealed class TreeSitterGrammarRegistry
{
    private readonly Lock _gate = new();
    private Snapshot _snapshot = new(0, ImmutableDictionary<string, TreeSitterGrammarRegistration>.Empty,
        ImmutableDictionary<string, string>.Empty);

    public void Register(TreeSitterGrammarRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Filetype);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.LanguageExport);
        if (!registration.LanguageExport.StartsWith("tree_sitter_", StringComparison.Ordinal) ||
            registration.LanguageExport.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("An exact tree_sitter_* language export is required.", nameof(registration));
        if (registration.Highlights.Count == 0) throw new ArgumentException("At least one highlight query is required.", nameof(registration));
        foreach (var asset in registration.Highlights.Concat(registration.Injections ?? []).Prepend(registration.Wasm))
        {
            if (!asset.Source.IsAbsoluteUri || asset.Source.Scheme is not ("https" or "file") ||
                asset.Source.UserInfo.Length != 0 || asset.Source.Fragment.Length != 0)
                throw new ArgumentException("Assets require absolute HTTPS or explicit local file URIs.", nameof(registration));
            if (asset.Sha256.Length != 64 || asset.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
                throw new ArgumentException("Every grammar/query asset requires a SHA-256 pin.", nameof(registration));
            ArgumentException.ThrowIfNullOrWhiteSpace(asset.Provenance, nameof(registration));
            ArgumentException.ThrowIfNullOrWhiteSpace(asset.License, nameof(registration));
        }
        var copy = registration with
        {
            Highlights = registration.Highlights.ToImmutableArray(),
            Injections = (registration.Injections ?? []).ToImmutableArray(),
            Aliases = (registration.Aliases ?? []).Where(alias => alias != registration.Filetype).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            InjectionNodeTypes = registration.InjectionNodeTypes?.ToImmutableDictionary(StringComparer.Ordinal),
            InjectionInfoStrings = registration.InjectionInfoStrings?.ToImmutableDictionary(StringComparer.Ordinal),
        };
        foreach (var alias in copy.Aliases!) ArgumentException.ThrowIfNullOrWhiteSpace(alias, nameof(registration));
        lock (_gate)
        {
            var aliases = _snapshot.Aliases.RemoveRange(_snapshot.Aliases.Where(pair => pair.Value == copy.Filetype).Select(pair => pair.Key));
            aliases = aliases.Remove(copy.Filetype);
            foreach (var alias in copy.Aliases!) aliases = aliases.SetItem(alias, copy.Filetype);
            _snapshot = new(checked(_snapshot.Revision + 1), _snapshot.Registrations.SetItem(copy.Filetype, copy), aliases);
        }
    }

    internal Snapshot Capture() { lock (_gate) return _snapshot; }
    internal sealed record Snapshot(long Revision, ImmutableDictionary<string, TreeSitterGrammarRegistration> Registrations,
        ImmutableDictionary<string, string> Aliases)
    {
        internal string? Resolve(string name)
        {
            if (Registrations.ContainsKey(name) || name is "javascript" or "typescript" or "markdown" or "markdown_inline" or "zig") return name;
            if (Aliases.TryGetValue(name, out var canonical)) return canonical;
            var bundled = TreeSitterAssets.Language(name);
            // Replacing a built-in also replaces its aliases; do not resurrect
            // removed aliases through the bundled fallback map.
            return bundled is not null && !Registrations.ContainsKey(bundled) ? bundled : null;
        }
    }
}
