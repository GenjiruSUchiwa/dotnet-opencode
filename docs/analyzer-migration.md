# Analyzer migration — in progress

This wave is **not complete**. There are now **eight parallel, disjoint owners**.
Each owner fixes diagnostics in its assigned paths; the EF owner also owns its
persistence analyzer cleanup. Independent owners do not wait for EF and must not
edit one another's files. The parent alone owns integration, commits, pushes,
releases, and the final full CLI dependency build/sign-off.

The latest parent-supplied full-graph baseline reported **1,979 warnings and
0 errors** in `provider-identity-build.log`, beneath the directory recorded by
`C:\tmp\opencode\modernization-integration-location.txt`. This is a historical
baseline, not a count of work currently remaining across concurrent owners.

### Core foundations handoff

The foundation owner has finished enabled-diagnostic cleanup in
`Core/{Agent,CodeMode,Commands,Config,Filesystem,Instructions,Llm,Plugins,Snapshot,Vcs}`
and `Core/Locations` **except CatalogLocation.cs**. Its isolated Core build reports
**0 diagnostics in that subset**, from **320 unique baseline warnings**, without
changing global analyzer policy, project/package configuration, or generated assets.
The same build succeeded with **10 warnings and 0 errors overall**; its remaining
warnings belong to other owners. This is not full-graph sign-off.

Details, exact exceptions, the two source-parity fixes, modified-file inventory,
and build evidence are in [Core foundations pass](core-foundations-pass.md).
ProviderUserAgent.Apply and the parent-approved leading `dotnet-opencode` identity
with configured metadata suffix remain intact.

## Package and build policy

- Official NuGet flat-container metadata identifies **Meziantou.Analyzer 3.0.203**
  as the current stable version at inspection. Its nuspec declares MIT licensing,
  .NET Standard 2.0 tooling, and source commit
  `a5098ce56986ebc07e631ef142e46551dc70b0ae`.
- `Directory.Build.props` pins that version for every owned `src` project using
  `PrivateAssets="all"` and build/analyzer/build-transitive assets only. It does
  not add a runtime application package dependency or change test projects.
- .NET analyzers use analysis level 11.0. Meziantou uses its **Default** policy,
  not `None`. Vogen references/analyzers remain enabled and unchanged.
- `.editorconfig` makes CA2008, CA2012, CA2013, CA2200, MA0040, MA0042, MA0166 and
  MA0188 build warnings in owned source. Other Meziantou defaults remain active
  except the individually documented compatibility/design choices below.
- Generated `.g.cs` and `.Generated.cs` inputs are marked generated. Generated
  output and pinned assets must not be edited to satisfy diagnostics.

The package loaded and analyzed the full CLI graph with local SDK
`11.0.100-preview.7.26381.103`; no analyzer-load/compiler-version errors occurred.
This establishes tooling compatibility with that compiler, not runtime correctness.

## Earlier-wave inventory (historical)

The full default-package baseline reported **6,263 warnings and 0 errors** across
the graph. The subsequent policy build reported **1,477 warnings and 0 errors**;
that count predates the most recent scope corrections and code fixes and is **not
the current remaining count**. Both include all owned application/library projects:
Schema, Core, Protocol, Server, Client, SDK, CLI, OpenTui.Blazor, OpenTui.Native,
Runtime.Time and Transport.Pipelines.

Build logs retained outside the repository:

- `C:\tmp\opencode\analyzer-baseline.log`
- `C:\tmp\opencode\analyzer-policy.log`
- Isolated artifacts: `C:\tmp\opencode\analyzer-build`

Outstanding families include explicit cultures/order comparers, regex analysis,
async continuation policy at reusable boundaries, intentional cancellation and
completed-task observations, task ownership, native enum-zero uses, and several
design/default-argument diagnostics. No diagnostic family is declared resolved
merely because it does not appear in a truncated console preview.

An independent **Rebuild** of `OpenTui.Blazor.csproj` and its dependencies now
reports **0 warnings and 0 errors** for OpenTui.Blazor, OpenTui.Native,
Transport.Pipelines and Runtime.Time. Log: `C:\tmp\opencode\analyzer-blazor.log`.
This does not compile the EF-owned persistence graph and is not full-wave sign-off.

The earlier isolated CLI compile reported **16 warnings and 0 errors**, all in
`src/OpenCode.Cli/Auth/AuthCommands.cs`. It used `BuildProjectReferences=false`
and the existing dependency outputs, so it does **not** verify the current EF
source. Log: `C:\tmp\opencode\analyzer-cli-isolated.log`.

| Rule | Count | Remaining scope in AuthCommands.cs |
| --- | ---: | --- |
| MA0004 | 9 | RunAsync awaits/database disposal; ChooseAsync polling await |
| MA0015 | 6 | RunAsync argument validation; ChooseAsync noninteractive validation; OpenBrowser URI validation |
| MA0042 | 1 | RunAsync's synchronous CreateConnection disposal/bootstrap boundary |

That version of AuthCommands directly constructed SqliteDatabase and
CredentialStore and was left unchanged for its composition owner. These warning
locations refer to the earlier source, not the current parallel-owner residuals.

## Current explicit policy decisions

| Rule | Policy/reason |
| --- | --- |
| MA0006 | Direct C# string `==`/`!=` already uses ordinal semantics; do not rewrite the source's value comparisons for style |
| MA0002 | `report_only_non_ordinal=true`: retain known-ordinal hash/equality operations; culture-sensitive ordering still requires review |
| MA0008 | Do not change native or existing managed layouts to Auto as a performance-style migration |
| MA0016 / MA0046 | Preserve public collection and Action-event APIs; interface/EventHandler rewrites require a separate API decision |
| MA0048 / MA0051 | Source modules colocate records and keep ordered algorithms together; filenames and line-count thresholds are not build gates |
| MA0004, CLI TUI / OpenTui.Blazor | Preserve dispatcher/caller-context continuations; no blanket ConfigureAwait(false) rewrite |
| MA0004, Server / SDK | Preserve existing request/embedding callback context policy |
| MA0015, Schema / Protocol | Existing public validation text/property paths must not gain a new ArgumentException suffix |
| MA0097, Schema/Ids/Identifiers.cs only | SessionId retains ordinal CompareTo without widening its operator API; Vogen supplies typed equality |
| MA0158, Schema/Identifier.cs only | Keep the established ID generator's synchronization/clock/counter/random algorithm untouched |
| MA0065, OpenTui.Native only | Native value/handle field equality is not replaced with invented semantic equality/hashing |
| MA0099 | Enum zero without a named zero member remains valid ABI/BCL representation; never replace an empty FileAttributes mask with Normal (128) |

These are rule-specific choices, not package-wide NoWarn. Core's preliminary broad
MA0004 context exception was removed before the EF handoff so the new persistence
implementation is not silently exempted. MA0015 is not globally disabled; member
access rooted in a parameter is allowed by the analyzer's documented option.

## Changes completed before EF ownership transfer

- Private monitor objects used only by `lock` were changed to `System.Threading.Lock`
  where MA0158 identified a supported replacement. No Monitor.Wait/Pulse usage was
  present in those sites. Lock regions, reentrancy, statements and await boundaries
  were not reordered. Identifier's original monitor remains an explicit exception.
- The atomic lock-type edit touched existing Core DatabaseBootstrap/SqliteDatabase,
  integrations, tools, permissions and SessionSkillService as well as UI/server
  services. Those edits were complete **before** the ownership transfer; the EF
  owner must preserve or deliberately reconcile that dirty work.
- No ConfigureAwait(false), cancellation forwarding, culture, regex engine,
  schema, SQL migration or persistence-engine change has been applied mechanically.

## Earlier EF transfer boundary (historical)

EF owner session: **`ses_f914cea73ffeW5hdh5Lz1c5TIA`**.

The EF owner took **all Core persistence/query/projector/database files**,
**Server/SDK persistence DI**, and the needed EF package configuration. The analyzer
worker must not edit or race those implementations during the rewrite. Existing
TimeProvider and Vogen changes remain requirements, not optional cleanup.

This earlier allocation is superseded by the parent's eight-owner disjoint work
plan. The foundation owner now edits only its Core subset and these analyzer/
foundation records. Global `.editorconfig`, Directory.Build.props and package
configuration require parent approval for an exact change. Persistence remains
outside this owner's scope; its diagnostics must not be silently suppressed.

## Independent library cleanup completed

- `SequenceBuffer.Commit` retains its synchronous single-owner pipe flush. A
  Debug assertion records the proven completed-ValueTask invariant (writer pausing
  is disabled), rather than converting the operation into an unrelated async API.
- FrameConnection's shutdown joins explicitly use CancellationToken.None after
  lifetime cancellation. Forwarding the already-cancelled token there would skip
  reader/writer retirement and was deliberately not done.
- PipelineText stream adapters use async disposal with ConfigureAwait(false),
  retaining leave-open behavior, reader-before-adapter disposal, and final pipe
  completion order.
- Native clipboard polling explicitly preserves ConfigureAwait(true), matching
  previous continuation capture. Retirement polling explicitly uses no cancellation
  token so a running native operation cannot be freed early.
- Native and managed input enum-zero sites use their real named None members;
  numeric flag masks, UTF encodings, ABI field widths and native structures are
  unchanged.
- Terminal capability and cursor-response regexes retain their patterns/options
  and boolean-only matching, with NonBacktracking for linear evaluation. They use
  no backreferences/lookarounds or capture results. The fixed `f/F` plus one/two
  ASCII-digit key prefix is scanned directly with the same greedy prefix length.
  No new arbitrary regex timeout policy was imposed on these paths.
- Immutable string membership/removal now explicitly uses Ordinal, the previous
  default. Numeric parsing retains CurrentCulture where that was the previous
  default; boxed WASM numeric conversion names InvariantCulture without changing
  wasm32 numeric values.
- Grammar/keymap registration errors now identify the actual containing method
  argument, and wrap-width validation names `width`. This improves generic-library
  ArgumentException.ParamName labels without changing accepted values, exception
  types, or wire codecs. Component-property and compound-packet diagnostics retain
  their existing text via narrowly scoped MA0015 exceptions.
- CodeHighlightState keeps synchronous parse cancellation before joining its loop;
  the single MA0042 exception prevents a scheduling/ordering change.

Exact source exceptions added in this subset: MA0015 at
NativeEmbeddedTerminal.EncodeMouse, TuiImage.OnParametersSet,
TuiText.OnParametersSet, ScrollBox.OnParametersSet and
TuiRenderer.InvalidAttribute; MA0042 at CodeHighlightState.DisposeAsync's
synchronous Cancel call. These are not project-wide warning suppressions.

## CLI cleanup and exact exceptions

The isolated CLI compile found no remaining diagnostics outside AuthCommands.
This statement applies only to that compile, not to a fresh full dependency build.

- SessionPicker now expresses the existing stable two-pass ordering as primary
  pin order followed by descending update time. Equal-key source order is retained.
- Numeric formatting and parsing explicitly retain CurrentCulture where the old
  overload used it, including theme scale keys and visible tab numbers.
- Configuration and tab navigation callers explicitly supply CancellationToken.None
  for the optional *additional* token. The helpers still link their existing root
  and tab lifetimes. Initial reload is explicitly discarded only because its helper
  reports errors and records the task joined by shutdown; it is not unowned work.
- Stream/enumerator and browser cancellation-registration disposal are asynchronous.
  Explicit ConfigureAwait(true) preserves existing continuation capture; existing
  ConfigureAwait(false) sites remain unchanged. BrowserLogin's cancellation
  registration is disposed before its listener and cancellation sources.
- SubagentsPane shares its projection method between parameter updates and filter
  changes instead of calling a framework lifecycle method directly.
- Named zero enum constants keep their numeric values. FileAttributes.None is
  available in the pinned .NET 11 reference assemblies and is zero, unlike Normal.

### Regex review

NonBacktracking is used for the reviewed reasoning-title, diff-hunk, attachment
line-range, clipboard image label, API path placeholder, leader marker, device
code, and whitespace-collapse patterns. These sites read the match or individual
non-repeated capture groups, not repeated capture history. Existing anchors,
Unicode character classes, case/culture options and greedy lengths are retained.
Fixed ASCII drive prefixes are scanned directly. No finite timeout or new timeout
exception contract was introduced.

Two exact MA0009 exceptions retain engines needed by the existing patterns:

- `Tui/Keymap/TuiKeybindConfig.cs`, ExpandAliases: bounded alias alternatives and
  one boundary lookahead; no unbounded repetition. Preserve separator/lookahead
  replacement behavior.
- `Commands/Run/RunToolPresentation.cs`, subagent title casing: fixed-width
  `\b\w` with ECMAScript semantics. Do not replace its word definition with the
  Unicode default or combine unsupported ECMAScript/NonBacktracking options.

### Other source-scoped exceptions

All paths below are relative to `src/OpenCode.Cli`.

| Rule | Exact scope | Reason |
| --- | --- | --- |
| MA0004 | Nullable standalone lease declarations in CommandLine/LifecycleCommands.cs, Commands/Api/ApiCommand.cs and Commands/Statistics/StatisticsCommand.cs | Preserve existing disposal continuation policy without changing nullable host ownership |
| MA0042 | OpenCodeApp.DisposeAsync request cancellation; OpenCodeApp.StopRecoveryAsync waiter cancellation | Keep synchronous callbacks before shutdown joins; do not cancel independently owned server execution |
| MA0042 | ImagePreviewController.LoadAsync; SessionPicker.Search | Cancel the old generation synchronously before installing the next one |
| MA0042 | PermissionComposer pending-key cancellation; DirectoryMoveDialog.Close; SessionPicker.Close | Preserve dispatcher callback order before returning/notifying the parent |
| MA0042 | ImageSourceLoader.ReadBounded MemoryStream.Write | In-memory copy, not blocking I/O; retain cancellation at the stream-read boundary |
| MA0065 | Wordmark native-color comparison | Preserve CLR NativeRgba field/NaN equality without changing native structure or equality contracts |
| MA0015 | ApiRequestResolver.Operation/Interpolate; ApiCommand.RunAsync; RunCommand.RunAsync; RunFiles.PrepareAsync; RunToolPresentation.Normalize; StatisticsCommand.RunAsync | Keep existing source-compatible CLI/path error text, including actual file/path parameter identities |
| MA0015 | AttachmentEdits.Index; AttachmentTextMarks.Reconcile/Position | Preserve display-unit and nested mention diagnostics |
| MA0015 | FormComposer.OnParametersSet; DirectoryMoveDialog.OnParametersSet; DialogThemeList.OnInitialized | Validated names are Blazor parameters, not C# method parameters |
| MA0015 | IntegrationManager.SubmitTextAsync; TuiEditorKeymap.CreateCommands; TranscriptGrammarCatalog.Register; SessionTabPresentation.Parse | Retain displayed prompt/command/pin/color diagnostics rather than appending helper argument names |

Existing BL0012 exceptions in OpenCodeApp were retained, not introduced as part of
this analyzer cleanup. No blanket CLI MA0015 or async-rule suppression was added.

## Verification restrictions and final gate

Only source/package inspection, restore and repo-local .NET builds are authorized:

```text
.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj -f net11.0
  --artifacts-path C:\tmp\opencode\analyzer-build
  -p:NuGetAudit=false -p:OpenApiGenerateDocuments=false -v:minimal
```

No tests, analyzer sample invocations, app/DI execution, serializer/DB/API probes,
native/provider/model/MCP verification, commits, resets or delegation. A final
successful compile will establish the enabled diagnostic gate only, not behavioral
parity. Exact source-compatibility/ABI/owner-thread exceptions must be recorded as
they are reviewed; unfinished diagnostics are not exceptions.
