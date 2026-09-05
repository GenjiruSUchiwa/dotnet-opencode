# Explicit grammar registration and asset cache

The existing five bundled grammars are unchanged. Generic Blazor has no OpenCode
dependencies or added language defaults. The same `ICodeHighlighter` capture
contract remains in use.

## Production source

OpenTUI 0.5.9 ships JavaScript, TypeScript, Markdown, Markdown inline, and Zig.
The actual TUI session module calls `addDefaultParsers(parsers.parsers)` with
`packages/tui/src/parsers-config.ts`. These are application overrides, not more
OpenTUI built-ins. The client replaces registrations by filetype; the worker
removes stale aliases and invalidates reusable parsers. Canonical registrations
take priority over aliases. Highlight queries are concatenated in declared order.

The worker compiles highlights and optional injections, **not locals**. Disabled
HTML injection code and commented-out incompatible Python/PHP/HTML queries must
not be silently re-enabled. Vue's html_tags query precedes its vue query.

`TranscriptGrammarCatalog` records the 34 active remote production entries and
their exact origins/releases. It includes only source aliases (`diff`: udiff,
patch; `make`: makefile). It contains no fabricated asset hashes or language
exports and does not register or download anything just by being referenced.

## Usage and ownership

```csharp
var registry = new TreeSitterGrammarRegistry();
await using var cache = new TreeSitterGrammarCache(explicitCacheDirectory,
    approvedHttpsOrigins);

// reviewedPins maps each exact source URI to its inspected SHA-256, provenance,
// and license. The export name comes from static inspection of that grammar.
TranscriptGrammarCatalog.Register(registry, "csharp", verifiedLanguageExport,
    reviewedPins);

await using var highlighter = new TreeSitterHighlighter(registry, cache);
// Pass highlighter through the existing CodeHighlighter parameter.
```

Declaration order above ensures the highlighter is disposed before the cache.
The cache and registry are caller-owned and may serve several highlighters.
No root markup was changed. The existing default shared highlighter still serves
the five bundled assets; an explicitly supplied highlighter takes precedence.

For integrations not using OpenCode's source catalog, call `registry.Register`
with a `TreeSitterGrammarRegistration`. Each asset requires:

- An absolute HTTPS URI or explicit local file URI.
- An exact SHA-256 digest of the approved bytes.
- Nonempty provenance and license attribution.
- For HTTPS, a separately authorized exact origin in the cache constructor.

`LanguageExport` is mandatory because filetype and native grammar symbol can
differ (for example csharp versus c_sharp). The loader verifies that the module
has that export with `() -> i32` type; it never reports success for metadata alone.
The existing loader rejects unsupported imports/dylink dependencies with a real
failure instead of substituting a lexer.

## Registry lifecycle

Registration copies collections into immutable snapshots. Requests use one
snapshot, so concurrent registration cannot mix a new query with an old grammar.
Replacement removes that filetype's old aliases without deleting another
registration's aliases. Explicit canonical names win over aliases.

Changes take effect on the next highlight request, not midway through a parse.
The serialized highlighter retires its old store/parser/query caches before using
a new registry revision. This also releases grammar table/data segments that
cannot individually be unloaded from the existing shared-memory WASM host.
Registration itself does not trigger a UI rerender or change the capture-provider
interface; callers must request highlighting after changing configuration.

Cancellation reaches cache waiting, local/network reads, redirects, query asset
loading, and the existing parse/query callbacks. Cache disposal waits for its
active read. Wasmtime compilation remains non-interruptible; disposal waits for
the active highlighter request. Caller cancellation is not converted to empty
captures. Other failures produce HasParser=false plus the diagnostic, which the
Code state displays as plain source text.

## Cache and integrity

The explicit cache directory uses SHA-256 filenames, not URL basenames. Both
cache hits and downloads are hash-checked. Query assets use strict UTF-8 and are
joined in declaration order with newlines. Empty-only highlight queries fail.
Limits are 64 MiB per grammar and 8 MiB per query asset.

HTTPS redirects are followed only when each destination origin is authorized;
there is no broad wildcard, credential forwarding, or HTTPS-to-HTTP downgrade.
Release downloads may need a separately reviewed GitHub CDN origin. No redirect
destination was probed or assumed authorized during this work.

Only verified complete content is published via a same-directory temporary file
and rename. Cancellation/failure removes only that operation's temporary file.
Concurrent publication verifies the winner. Corrupt existing cache content is
reported; this code does not delete/overwrite user cache entries to hide errors.
There is no automatic refresh of floating URLs and no background download.

Unlike the source downloader, this implementation does not silently concatenate
only the successful subset when an integrity-pinned query dependency fails. The
whole grammar load fails, so it cannot claim correct captures from incomplete
queries. Source file order and explicit dependencies remain intact.

## Remaining boundary

**Production packaging update:** the CLI now packages 32 statically reviewed
production entries and uses them through a leased shared transcript highlighter.
See `OpenCode.Cli/Tui/Transcript/GrammarAssets/README.md` for actual pins, licenses,
exclusions, and verification limits. Generic default instances still contain
only the original five built-ins. `TreeSitterGrammarCache(..., offline: true)`
requires a verified content-addressed cache hit and never downloads or writes.
The following paragraph records the earlier registry-only batch:

No new remote grammar/query bytes or hashes were obtained in this batch. The
production catalog provides exact known origins, not an attestation that remote
bytes have been reviewed. New languages require supplied reviewed pins/export
metadata; a release tag or floating master URL alone is not an integrity pin.
No remote grammar becomes a default simply because its catalog entry exists.

Runtime loading, parsing, querying, redirects, cache I/O, and disposal were not
executed for verification. Validation is source inspection and the pinned .NET 11
full CLI build with isolated artifacts and `OpenApiGenerateDocuments=false`.
