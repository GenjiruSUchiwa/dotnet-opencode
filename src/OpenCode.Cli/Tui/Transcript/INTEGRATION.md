# Transcript Integration

This subtree contains native Razor components, not HTML or ANSI output. It does
not own client state, requests, key routing, theme resolution, or message delivery.

## Required Project Change

The integration owner must add this reference to `OpenCode.Cli.csproj`, then
restore. The project file was intentionally not edited by the transcript task.

```xml
<PackageReference Include="Markdig" Version="0.41.3" />
```

Markdig supplies the CommonMark AST, pipe tables, and emphasis extensions. No
regex Markdown parser or HTML renderer is used.

## App Parameters

```razor
@using OpenCode.Cli.Tui.Transcript

<SessionTranscript Messages="Messages"
                   Theme="TranscriptColors"
                   ScrollState="TranscriptScroll"
                   ShowReasoning="ShowReasoning"
                   ShowToolOutput="ShowToolOutput"
                   ExpandedRows="ExpandedTranscriptRows" />
```

- `Messages`: `IReadOnlyList<OpenCode.Schema.SessionMessage>`, in canonical history order. Pass only visible delivered history; apply revert and queued-input filtering in the caller.
- `Theme`: required `TranscriptTheme`, using resolved `#RRGGBB` or `#RRGGBBAA` colors. There are no guessed defaults.
- `ScrollState`: required `OpenTui.Blazor.TerminalScrollState`. Keep one per active transcript. Route scrolling to `ScrollBy`, end navigation to `ScrollToEnd`, and session changes to `Reset`.
- `ShowReasoning`: `bool`, default `false`. Expands all reasoning groups.
- `ShowToolOutput`: `bool`, default `false`. Expands all tool inputs and outputs.
- `ExpandedRows`: `IReadOnlySet<string>`, default empty. Replace the set to expand individual reasoning groups or tools. Obtain stable keys from `TranscriptRows.Create(Messages)`; group keys use their first part. The generic Box API does not currently expose click/focus callbacks, so the host must route expansion commands.

Use the component in the existing bounded, growing conversation area. It already
owns a `ScrollBox`; do not wrap it in a second scroll viewport.

## Theme Mapping

Map the palette by semantic role, not by a desired color:

| Parameter | Upstream role |
| --- | --- |
| Text | `text.default` |
| Subdued | `text.subdued` |
| Background | `background.default` |
| ElevatedBackground | elevated theme `background.default` |
| RaisedBackground | `raise(background.default)` |
| Border | `border.default` |
| Agent | selected agent's resolved color |
| Warning, Error, Success | corresponding `text.feedback` status colors |
| MarkdownText, MarkdownHeading, MarkdownCode, MarkdownLink, MarkdownQuote, MarkdownList | corresponding Markdown syntax colors |

Upstream sources: `packages/tui/src/routes/session/rows.ts` and the `UserMessage`,
`ReasoningPart`, `TextPart`, and tool presentations in `routes/session/index.tsx`.
User messages use the elevated surface, left agent border, two-column inner left
padding, and one row of vertical padding. Assistant content is indented three
columns. Reasoning is subdued inside a raised left border when expanded.

## Behavior And Boundaries

- Canonical messages retain distinct user, assistant-part, reasoning-group, exploration-group, and assistant-footer rows. Read/glob/grep calls group without hiding their individual statuses.
- Whitespace-only assistant parts and undescribed synthetic messages are omitted. Encrypted reasoning markers are removed. Consecutive reasoning can group across assistant messages.
- Tool streaming, running, completed, and error states are read directly from Schema. Error text remains visible when output is collapsed. Output is literal text, not reinterpreted as Markdown.
- Headings, nested lists, fenced/indented code, quotes, links, emphasis, and pipe tables render through native boxes and styled runs. Inline run arrays and Markdown documents are retained until their source or theme changes.
- Code language labels are shown, but language-specific syntax highlighting is not implemented. Images are identified by their supplied name and MIME type, not decoded or replaced with fake image content.
- Tables use equal flexible columns and native borders. Decimal/list ordinals come from the parsed AST. Table alignment directives and sophisticated narrow-terminal table layouts are not implemented.
- Pending inbox controls, permission interactions, usage aggregation, timeline actions, and tool-specific interactive panels remain caller/integration work, not simulated behavior.

## Verification

Attempted `dotnet build src/OpenCode.Cli/OpenCode.Cli.csproj --no-restore
-p:BuildProjectReferences=false --nologo`. It was blocked by the missing Markdig
reference and stale referenced assemblies (including missing `NativeTextRun`,
`TerminalScrollState`, `SessionHttpClient`, and dialog types used elsewhere).
An isolated Razor component build subsequently passed with **zero warnings and
zero errors**, using Markdig 0.41.3 and the current Schema/OpenTui project sources.
Its temporary project was removed. Build artifacts are outside the repository at
`C:/tmp/opencode/transcript-verify`.

No runtime or tests were started. A clean app integration build is still required.
