namespace OpenCode.Cli.Tui.Components;

using Microsoft.AspNetCore.Components;
using OpenCode.Cli.Tui.ToolViews;
using OpenCode.Cli.Tui.Transcript;
using OpenCode.Schema;
using OpenTui.Blazor.Code;
using OpenTui.Native;

public partial class OpenCodeApp
{
    [Parameter] public ICodeHighlighter? TranscriptCodeHighlighter { get; set; }
    private ToolDiffView _toolDiffView = ToolDiffView.Auto;
    private NativeTextWrapMode _toolDiffWrap = NativeTextWrapMode.Word;

    private ToolViewBindings TranscriptToolBindings => new()
    {
        DiffView = _toolDiffView,
        DiffWrap = _toolDiffWrap,
        DiffColors = ToolDiffColors.From(ElevatedColors),
        CodeHighlighter = TranscriptCodeHighlighter,
        SyntaxRules = TranscriptSyntax.Rules(ElevatedColors),
        Filetype = ToolFiletype,
        NavigateSession = OpenTabSession is null ? null : OpenActivitySession,
        IsSessionRunning = ReadToolSessionRunning
    };

    private bool? ReadToolSessionRunning(SessionId id) => ReadSessionObservation?.Invoke(id) is
        { Deleted: false, Error: null, Synchronization: SessionSynchronization.Live } observation ? observation.Running : null;

    // Source util/filetype.ts: exact extension mapping, including JavaScript/React -> TypeScript.
    // This is classification only. The shared provider decides whether a grammar is bundled.
    private static string? ToolFiletype(string path)
    {
        if (path.Length == 0) return "none";
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        return name[dot..] switch
        {
            ".abap" => "abap", ".bat" => "bat", ".bib" or ".bibtex" => "bibtex",
            ".clj" or ".cljs" or ".cljc" or ".edn" => "clojure", ".coffee" => "coffeescript",
            ".c" => "c", ".cpp" or ".cxx" or ".cc" or ".c++" => "cpp", ".cs" or ".csx" => "csharp",
            ".css" => "css", ".d" => "d", ".pas" or ".pascal" => "pascal", ".diff" or ".patch" => "diff",
            ".dart" => "dart", ".dockerfile" => "dockerfile", ".ex" or ".exs" => "elixir", ".erl" or ".hrl" => "erlang",
            ".fs" or ".fsi" or ".fsx" or ".fsscript" => "fsharp", ".gitcommit" => "git-commit", ".gitrebase" => "git-rebase",
            ".go" => "go", ".groovy" => "groovy", ".gleam" => "gleam", ".hbs" or ".handlebars" => "handlebars",
            ".hs" or ".lhs" => "haskell", ".html" or ".htm" => "html", ".ini" => "ini", ".java" => "java",
            ".jl" => "julia", ".kt" or ".kts" => "kotlin", ".json" => "json", ".tex" or ".latex" => "latex",
            ".less" => "less", ".lua" => "lua", ".makefile" => "makefile", ".md" or ".markdown" => "markdown",
            ".m" => "objective-c", ".mm" => "objective-cpp", ".pl" or ".pm" => "perl", ".pm6" => "perl6",
            ".php" => "php", ".ps1" or ".psm1" => "powershell", ".pug" or ".jade" => "jade", ".py" => "python",
            ".r" => "r", ".cshtml" or ".razor" => "razor", ".rb" or ".rake" or ".gemspec" or ".ru" => "ruby",
            ".erb" => "erb", ".rs" => "rust", ".scss" => "scss", ".sass" => "sass", ".scala" => "scala",
            ".shader" => "shaderlab", ".sh" or ".bash" or ".zsh" or ".ksh" => "bash", ".sql" => "sql",
            ".svelte" => "svelte", ".swift" => "swift",
            ".ets" or ".js" or ".jsx" or ".ts" or ".tsx" or ".mts" or ".cts" or ".mtsx" or ".ctsx" or ".mjs" or ".cjs" => "typescript",
            ".xml" => "xml", ".xsl" => "xsl", ".yaml" or ".yml" => "yaml", ".vue" => "vue", ".zig" or ".zon" => "zig",
            ".astro" => "astro", ".ml" or ".mli" => "ocaml", ".tf" => "terraform", ".tfvars" => "terraform-vars",
            ".hcl" => "hcl", ".nix" => "nix", ".typ" or ".typc" => "typst", _ => null
        };
    }
}
