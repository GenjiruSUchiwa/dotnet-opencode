# Dedicated tool results and patch diff views

## Scope and mounted path

Only the new `CLI/Tui/ToolViews` subtree and `CLI/Tui/Transcript/ToolRow.razor` were changed. Completed Stash/SystemDialogs/Skills, root files, `SessionTranscript`, Markdown, `TuiCode`, parser implementation, shared project files, and server/core code were not edited.

This is connected, not a standalone unmounted renderer: both existing `SessionTranscript` call sites already render `ToolRow`. That row now selects `ToolEditView`, `QuestionToolView`, or `SubagentToolView` directly. The canonical aliases are exactly source `bash → shell`, `task → subagent`, and `apply_patch → patch`. Arbitrary tool names/commands are not inspected to guess patches or subagent sessions.

`Expanded` remains the existing effective global/per-row state supplied by `SessionTranscript`. These components introduce no competing expansion state. Detailed diff, question answer, and subagent content obey that value; headers remain visible. Existing transcript keys/toggle callbacks are unchanged. File, hunk, line, pair, question, and content keys are stable positional identities within their actual tool ID, not text-derived keys that change when streaming content updates.

## Diff source mapping

- `packages/tui/src/routes/session/index.tsx`: `Edit`, `ApplyPatch`, `parseApplyPatchFiles`.
- `packages/tui/src/component/patch-diff.tsx` and `util/diff.ts`: independent hunks, hidden first hunk header, visible later headers, aligned gutter widths.
- Installed `@opentui/core/index.bun.js`, `DiffRenderable.buildUnifiedView/buildSplitView`, and its bundled `diff@9` hunk parser: old/new numbering, one-number unified gutter, contiguous add/remove block pairing by ordinal, blank counterpart rows, no-newline markers omitted from display but retained as pairing boundaries.

**The production source path contains no character/word-difference intraline algorithm.** It pairs lines positionally and colors whole changed lines. No Myers/LCS, word lexer, common-prefix heuristic, or replacement intraline diff was invented. Within-line syntax spans are consumed only from the existing abstract capture provider and shared `CodeProjection` implementation.

`PatchDocument` validates hunk ranges/counts before producing rows. Invalid hunks display a parse error and the exact supplied patch text, not a generated replacement. It parses the first file from a file's metadata patch, as the source renderer does. Patch application, filesystem reads, and before/after content reconstruction are not performed. CRLF separators are normalized for parsing. Metadata without a hunk (for example an empty or binary diff) does not invent textual changes.

The default view is **auto: split only above 120 transcript columns; unified otherwise**, with source word wrapping. Explicit Split/Unified and None/Word/Character wrapping can be supplied by the host bindings. Split halves use the available width; the right half receives an odd extra column. Extremely narrow explicit split views clip gutters before consuming the last content column.

Native text views perform wrapping and selection. Each logical pair is a native flex row whose height is the taller side; shorter sides leave unnumbered context padding. No managed approximation of Unicode terminal width or manual wrapping is used. Gutters/signs are separate non-selectable nodes, only source content is selectable. Wrapped continuation rows do not repeat line numbers. No selection/copy/clipboard handler was added.

`ToolEditView` uses only validated `metadata.files` records (type/status, file/relativePath/filePath, patch, finite additions/deletions, optional movePath). Edit uses the first file, matching source. Patch displays all files, source Created/Deleted/Patched labels, and deletion counts instead of a made-up deleted-file body. `metadata.applied` is the source metadata-only fallback. Pending patch target labels come only from the canonical `input.patchText` Add/Update/Delete File directives; no patch preview is made from pending input. Edit diagnostics use actual severity-1 metadata and are capped at three. Diagnostic lookup currently uses the supplied input path exactly; root/path-owner normalization is not reimplemented here.

## Optional root cascade

No new root callbacks are required for basic diff/results to appear. To pass configured view, full colors, a ready parser, or real navigation **without changing SessionTranscript's parameter forwarding**, root can wrap its existing transcript in a typed cascade:

```razor
<CascadingValue Value="toolViewBindings">
    @* The existing SessionTranscript mount and parameters stay here. *@
</CascadingValue>
```

Use a root-owned `ToolViewBindings` value:

```csharp
new ToolViewBindings
{
    DiffView = configuredDiffView,        // Auto / Unified / Split
    DiffWrap = configuredNativeWrapMode,
    DiffColors = ToolDiffColors.From(ElevatedColors),
    CodeHighlighter = existingReadyCaptureProvider,
    SyntaxRules = existingSyntaxRules,
    Filetype = existingSourceFiletypeResolver,
    NavigateSession = existingSessionNavigation,
    IsSessionRunning = readSharedSessionRunning,
};
```

These names describe owner-supplied values, not new implemented root fields. Replace the bindings value when config/theme/shared state changes. Do not create a config reader, preference writer, SSE subscription, session client, parser runtime, or native-probing loop for these rows.

`ToolRow.ViewBindings` is an optional direct override for other legitimate row callers. Otherwise it reads the typed `HostBindings` cascade.

### Theme and syntax dependencies

`ToolDiffColors.From` resolves existing semantic roles only: `diff.background.*`, `diff.highlight.*`, and `diff.lineNumber.*`. There are no raw colors or borrowed success/error colors for changes. The current root `TranscriptTheme` already provides `diff.text.added/removed/hunkHeader`, so unbound mounted rows still show diff text and signs, numbers, alignment, and content. **The current root does not pass diff background/line-number roles**: until its owner adds the cascade, those remain unset rather than synthesized. `Theme.Subdued` supplies the ordinary neutral gutter fallback.

Only the existing `ICodeHighlighter`, `CodeHighlightState`, `CodeProjection`, and capture/style contracts are consumed. The diff never creates or owns `TreeSitterHighlighter`, a Wasmtime engine, or a substitute lexer. A ready provider plus its actual filetype resolver and syntax rules is optional. Without them the source content renders unhighlighted. The filename is passed to the owner resolver; no unsupported language is advertised as parsed. Provider failures retain readable text and expose diagnostics.

Each hunk is highlighted as a whole side (unified or left/right) before its real UTF-16 capture/style spans are sliced into immutable native text runs. There is no per-line parser call that loses multiline context. Source Diff's default `Conceal=false` is preserved so source offsets/line numbers remain valid. Component disposal cancels its own outstanding capture request; it never disposes the borrowed provider. Wasmtime implementation and verification remain with its assigned owner.

### Question and subagent results

- Questions read only `input.questions[].question` and `metadata.answers` arrays. Source answer filtering, order, comma-joined multiple answers, and `(no answer)` are preserved. No reply/approve UI or fabricated answer is created. Missing answers remain a pending/count summary; any actual terminal output can be inspected when expanded.
- Subagents read `metadata.sessionID` or source compatibility `sessionId`, `input.description`, `agent`/`subagent_type`, and continuation `input.sessionID`. Completed tool state with `metadata.status == "running"` shows source **Background**. A child is treated as live-running only when the canonical tool itself is running or the supplied shared-state callback says so; no inferred healthy/completed child result is substituted.
- The session identity and actual tool content remain available read-only. **An Open subagent action exists only when a valid canonical Session ID and a real `NavigateSession` delegate are both available.** Its event is handled before awaiting to avoid toggling the parent row; navigation failure is displayed. Without the delegate, there is no fake button, external URL, or API call. No interrupt/retry action was added.

## Build-only verification

The repository-local pinned .NET 11 SDK was used with isolated `C:\tmp\opencode\mcp-finish-pass` artifacts and OpenAPI document execution disabled:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj --artifacts-path C:\tmp\opencode\mcp-finish-pass --no-restore -p:OpenApiGenerateDocuments=false -v:minimal
```

The full graph build was blocked before CLI compilation by concurrent, out-of-scope work:

- `OpenTui.Blazor/Code/TreeSitterWasm.cs`: Wasmtime namespace/types missing from the current isolated restore graph.
- `OpenCode.Core/Event/Log/DurableReplayDefinitions.cs`: mismatched durable definition members/types.
- `OpenCode.Core/Tools/ShellParsing/PowerShellExpressions.cs`: named arguments not yet present on the shared parser helpers.

A first CLI-only attempt was blocked by another owner's `Commands/Run/RunMimeRegistry.cs:14` (`Types` not defined). The final CLI-only build with `-p:BuildProjectReferences=false` then **succeeded with 0 warnings and 0 errors** against the existing isolated dependency outputs. This compiled the final ToolViews/ToolRow code; it did not rebuild/verify the concurrently changing dependencies. **The last full-graph attempt remains blocked as listed above.** Do not claim runtime, rendering, native, parser, or selection verification from this work.

No tests were added, edited, or run. No app/TUI execution, visual/native verification, API/DB/network interaction, process execution other than the permitted builds, state/config file reads or writes, commits, or delegation was performed.
