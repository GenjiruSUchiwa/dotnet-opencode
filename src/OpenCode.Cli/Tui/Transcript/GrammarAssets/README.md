# Reviewed production grammar assets

This directory packages 32 of the 34 remote registrations declared by the actual
`packages/tui/src/parsers-config.ts`. The five OpenTUI built-ins remain in
OpenTui.Blazor and are unchanged. These are upstream parser/query bytes, not
application or business code compiled to WASM.

## Pins and provenance

- `manifest.json`: exact original HTTPS sources, actual SHA-256 digests, byte
  counts, ordered queries, source aliases, inspected language exports, grammar
  and query license origins/hashes, retrieval time, and source-config digest.
- `content/<sha256>`: unchanged asset bytes. The 65 files total 61,423,959 bytes
  (32 grammars and 33 queries; Vue has two ordered query files).
- `licenses/`: retrieved grammar and query repository license text, unchanged.
- `retrieval.json`: results for all 34 grammar and 35 query downloads. Expiring
  redirect/CDN URL credentials are deliberately not retained here.
- `wasm-metadata.json`: static exports, imports, function types and dylink.0
  allocation metadata for all retrieved grammars.
- `import-review.json`: static function-signature comparison with the pinned
  web-tree-sitter runtime and the explicit host callbacks.
- `license-retrieval.json`: grammar license origins and failed retrievals.

The declared URLs were retrieved only as build dependencies/source inspection
into `C:\tmp\opencode\reviewed-grammars`. No Wasmtime engine or module was loaded,
instantiated, parsed, queried, or executed to generate this review. The manifest
digest is pinned in `ProductionGrammarHighlighter.cs`.

Mutable upstream query URLs are recorded for provenance only. Their downloaded
bytes are pinned and packaged; the application never refreshes them implicitly.
Highlight-query order and source contents are retained, including source query
dependencies/limitations. Locals queries are not added: the production worker
does not compile them. Disabled source HTML injections remain disabled.

## Default selection and lifetime

`MarkdownBlocks` obtains a lease on one `ProductionGrammarHighlighter` shared
across mounted transcript code views. Registration is performed once when that
client is first used, not once per row. The existing explicitly supplied
`CodeHighlighter` parameter still overrides production selection. No root or
preference integration was changed.

The host uses the packaged content directory in **offline mode**. Manifest and
asset digest checks remain active. A missing or corrupt file cannot cause a
network fetch, silent update, or closest-language substitution. Unsupported
languages and loading/query failures produce `HasParser=false` and the existing
plain-source fallback. Only successful parsing/querying produces captures.
The last lease drains and disposes the shared highlighter before its asset cache.

Build/publish copies the assets and licenses with ordinary MSBuild content items;
there is no custom target that runs an application, parser, native library, or
WASM module. The output/publish directory must retain this asset subtree.

## Excluded registrations

Both binaries and their declared queries were retrieved successfully, but these
registrations are **not enabled or packaged as parser assets**:

- **clojure**: the declared fork release `anomalyco/tree-sitter-clojure/v0.0.1`
  returned 404 for LICENSE, LICENSE.md, LICENSE.txt, and COPYING. No license was
  substituted from an unrelated fork/revision.
- **nix**: the declared binary is vendored under the pinned ast-grep website
  commit, not a grammar release. Grammar-license provenance for that binary was
  not established; the website's license was not assumed to cover it.

Their source metadata remains in the review reports, and the manifest records
their exclusion. No dummy registration or closest-language fallback is supplied.

## Verification boundary

All retrieved grammars have dylink.0 allocation metadata and one inspected
`tree_sitter_*` export of type `() -> i32`. Their imported function signatures
match the pinned runtime exports or explicit callbacks. Kotlin, XML, and YAML
add the inspected `env.abort: () -> ()` callback, which raises a real error.

This establishes asset identity and the inspected ABI surface, **not runtime
grammar compatibility or query correctness**. In particular, mutable source
queries may contain incompatible node names/directives. They were not rewritten,
parsed, or executed during verification; any actual query compilation error
remains a plain-text fallback with a diagnostic.

Full pinned .NET 11 CLI build succeeded with 0 warnings and 0 errors using
`OpenApiGenerateDocuments=false` and isolated artifacts at
`C:\tmp\opencode\reviewed-grammar-build`. No tests or runtime verification ran.
