namespace OpenCode.Cli.Tui.Transcript;

using System.Collections.Immutable;
using OpenTui.Blazor.Code;

/// <summary>Production TUI registrations, not extra built-ins. No downloads or registration occur on access.</summary>
public static class TranscriptGrammarCatalog
{
    public sealed record Source(string Filetype, Uri Wasm, ImmutableArray<Uri> Highlights, ImmutableArray<string> Aliases);

    // packages/tui/src/parsers-config.ts, registered by routes/session/index.tsx:
    // addDefaultParsers(parsers.parsers). Locals are intentionally absent: the
    // production worker compiles highlights and injections, not locals queries.
    public static ImmutableArray<Source> Sources { get; } =
    [
        Grammar("python", "tree-sitter/tree-sitter-python", "v0.23.6", queries: ["https://github.com/tree-sitter/tree-sitter-python/raw/refs/heads/master/queries/highlights.scm"]),
        Grammar("rust", "tree-sitter/tree-sitter-rust", "v0.24.0"),
        Grammar("go", "tree-sitter/tree-sitter-go", "v0.25.0"),
        Grammar("cpp", "tree-sitter/tree-sitter-cpp", "v0.23.4"),
        Grammar("csharp", "tree-sitter/tree-sitter-c-sharp", "v0.23.1", binary: "c_sharp", query: "c_sharp"),
        Grammar("bash", "tree-sitter/tree-sitter-bash", "v0.25.0"),
        Grammar("c", "tree-sitter/tree-sitter-c", "v0.24.1"),
        Grammar("java", "tree-sitter/tree-sitter-java", "v0.23.5"),
        Grammar("kotlin", "fwcd/tree-sitter-kotlin", "0.3.8", queries: ["https://raw.githubusercontent.com/fwcd/tree-sitter-kotlin/0.3.8/queries/highlights.scm"]),
        Grammar("ruby", "tree-sitter/tree-sitter-ruby", "v0.23.1"),
        Grammar("php", "tree-sitter/tree-sitter-php", "v0.24.2", queries: ["https://github.com/tree-sitter/tree-sitter-php/raw/refs/heads/master/queries/highlights.scm"]),
        Grammar("scala", "tree-sitter/tree-sitter-scala", "v0.24.0"),
        Grammar("html", "tree-sitter/tree-sitter-html", "v0.23.2", queries: ["https://github.com/tree-sitter/tree-sitter-html/raw/refs/heads/master/queries/highlights.scm"]),
        Grammar("vue", "anomalyco/tree-sitter-vue", "v0.1.2", queries: [
            "https://raw.githubusercontent.com/anomalyco/tree-sitter-vue/v0.1.2/queries/html_tags/highlights.scm",
            "https://raw.githubusercontent.com/anomalyco/tree-sitter-vue/v0.1.2/queries/vue/highlights.scm"]),
        Grammar("hcl", "tree-sitter-grammars/tree-sitter-hcl", "v1.2.0", queries: ["https://raw.githubusercontent.com/nvim-treesitter/nvim-treesitter/master/queries/hcl/highlights.scm"]),
        Grammar("json", "tree-sitter/tree-sitter-json", "v0.24.8"),
        Grammar("yaml", "tree-sitter-grammars/tree-sitter-yaml", "v0.7.2"),
        Grammar("haskell", "tree-sitter/tree-sitter-haskell", "v0.23.1"),
        Grammar("css", "tree-sitter/tree-sitter-css", "v0.25.0"),
        Grammar("julia", "tree-sitter/tree-sitter-julia", "v0.23.1"),
        Grammar("lua", "tree-sitter-grammars/tree-sitter-lua", "v0.5.0", queries: ["https://raw.githubusercontent.com/tree-sitter-grammars/tree-sitter-lua/v0.5.0/queries/highlights.scm"]),
        Grammar("ocaml", "tree-sitter/tree-sitter-ocaml", "v0.24.2"),
        Grammar("clojure", "anomalyco/tree-sitter-clojure", "v0.0.1"),
        Grammar("swift", "alex-pinkus/tree-sitter-swift", "0.7.1", queries: ["https://raw.githubusercontent.com/alex-pinkus/tree-sitter-swift/main/queries/highlights.scm"]),
        Grammar("toml", "tree-sitter-grammars/tree-sitter-toml", "v0.7.0", queries: ["https://raw.githubusercontent.com/nvim-treesitter/nvim-treesitter/master/queries/toml/highlights.scm"]),
        new("nix", new("https://github.com/ast-grep/ast-grep.github.io/raw/40b84530640aa83a0d34a20a2b0623d7b8e5ea97/website/public/parsers/tree-sitter-nix.wasm"), [Nvim("nix")], []),
        Grammar("diff", "tree-sitter-grammars/tree-sitter-diff", "v0.1.0", aliases: ["udiff", "patch"], queries: ["https://raw.githubusercontent.com/tree-sitter-grammars/tree-sitter-diff/master/queries/highlights.scm"]),
        Grammar("elixir", "elixir-lang/tree-sitter-elixir", "v0.3.5"),
        Grammar("fsharp", "ionide/tree-sitter-fsharp", "0.3.0"),
        Grammar("r", "r-lib/tree-sitter-r", "v1.2.0"),
        Grammar("make", "tree-sitter-grammars/tree-sitter-make", "v1.1.1", aliases: ["makefile"]),
        Grammar("vim", "tree-sitter-grammars/tree-sitter-vim", "v0.8.1"),
        Grammar("xml", "tree-sitter-grammars/tree-sitter-xml", "v0.7.0"),
        Grammar("agda", "tree-sitter/tree-sitter-agda", "v1.3.3"),
    ];

    /// <summary>Registers only an explicit production entry after every source has a reviewed pin.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The missing-pin diagnostic identifies the source URI, not a C# helper parameter.")]
    public static void Register(TreeSitterGrammarRegistry registry, string filetype, string verifiedLanguageExport,
        IReadOnlyDictionary<Uri, TreeSitterGrammarAsset> pins)
    {
        var source = Sources.Single(entry => entry.Filetype == filetype);
        TreeSitterGrammarAsset Require(Uri origin) => pins.TryGetValue(origin, out var pin) && pin.Source == origin
            ? pin : throw new ArgumentException($"Missing reviewed grammar asset pin for {origin}.");
        registry.Register(new(source.Filetype, verifiedLanguageExport, Require(source.Wasm),
            source.Highlights.Select(Require).ToImmutableArray(), source.Aliases));
    }

    private static Uri Nvim(string filetype) => new($"https://raw.githubusercontent.com/nvim-treesitter/nvim-treesitter/refs/heads/master/queries/{filetype}/highlights.scm");
    private static Source Grammar(string filetype, string repository, string version, string? binary = null,
        string? query = null, string[]? queries = null, string[]? aliases = null) =>
        new(filetype, new($"https://github.com/{repository}/releases/download/{version}/tree-sitter-{binary ?? filetype}.wasm"),
            queries is null ? [Nvim(query ?? filetype)] : queries.Select(value => new Uri(value)).ToImmutableArray(),
            (aliases ?? []).ToImmutableArray());
}
