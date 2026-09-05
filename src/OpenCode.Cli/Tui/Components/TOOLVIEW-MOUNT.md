# Tool-result host bindings

The existing SessionTranscript is wrapped in a `ToolViewBindings` cascade. Its
global/per-row expansion values, keys, and toggle callbacks are unchanged.

- Full diff backgrounds, signs, and line-number roles use
  `ToolDiffColors.From(ElevatedColors)`. Syntax rules use the same elevated context
  for tool diffs; Markdown keeps its existing base-context rules.
- `diffs.view` and `diffs.wrap` are registered with the existing settings controller
  and writer. Source choices are auto/split/unified and none/word, with auto/word
  defaults. The consumers change the actual cascade and request rendering. Loading
  or registering them does not write defaults or replace user preferences.
- `app.toggle.diffwrap` uses that same registered setting's change operation. No
  unsupported viewer settings or no-op callbacks are registered.
- `InteractiveTui` owns one lazy `TreeSitterHighlighter` shared by Markdown fences
  and tool diff hunks. Supplying it to SessionTranscript bypasses those TuiCode
  views' default-provider acquisition. Components dispose/drain before the provider.
  There is no engine per render, row, or hunk and no parser owned by ToolViews.
- Filename classification follows `util/filetype.ts`, including its
  JavaScript/React-to-TypeScript mapping. It does not inspect content or read files.
  The actual provider reports unsupported grammars; no fallback lexer is used.
- Subagent navigation is supplied only when the existing `OpenTabSession` callback
  is connected. It uses `OpenActivitySession` and the current observer/hydration
  route. Running state comes from a live, error-free shared Session observation;
  unavailable state returns null rather than a guessed idle/completed result.

Line pairing and expansion remain owned by the supplied ToolViews implementation.
No intraline difference algorithm, patch application, new observer, client, settings
store, or preference writer is introduced.

Verification is limited to pinned repo-local .NET 11 full CLI builds with isolated
artifacts and `OpenApiGenerateDocuments=false`. No tests, app/native/parser execution,
visual checks, API/DB/clipboard calls, or preference I/O are used for verification.

Latest full CLI build is blocked before CLI compilation by concurrent
`OpenCode.Core/Integrations/Wellknown/WellknownTransport.cs:71` (`CS0019`, null
coalescing an IReadOnlyDictionary with a collection expression). That owner file
was not edited. This batch does not claim a successful final dependency-graph build.
