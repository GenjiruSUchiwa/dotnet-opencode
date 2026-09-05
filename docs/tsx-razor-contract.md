# TSX → Razor contract: Home, composer, selection, one prompt

## Scope and evidence

Read-only comparison; **no application or engine implementation changes**. This
report is frozen pending the explicit engine handoff from
`ses_f94601412ffejkGvo26NX6diBl`. Engine files were being edited concurrently: the
findings describe the inspected members, not a claim about that owner's final API.
No builds, tests, JS/TUI/native/clipboard/DI/DB/provider execution, live configuration
or credential reads, Git operations, or delegation were performed in this phase.

Truth: `C:\Repos\sst\kind-nebula\packages\tui\src` and its installed
`@opentui/core` **0.5.9**. Primary references:

- `routes/home.tsx:75–108`; `component/prompt/index.tsx:1580–2065`.
- `routes/session/index.tsx:1358–1506`; `routes/session/composer/index.tsx`.
- `ui/border.ts`; `component/logo.tsx`; built-in `feature-plugins/home/footer.tsx`
  and `feature-plugins/prompt/footer.tsx`. Footer slots are not assumed empty.
- Installed `chunk-bun-jxfx3h5k.js`: `setupYogaProperties` (579–641), Box defaults
  (2543–2575), text defaults (2803–2826), editor defaults (5538–5575), movement
  (6025 onward). `chunk-bun-b0662dgp.js` supplies the alignment parsers.

Agent notes `selection-color-fix.md`, `word-selection-fix.md`, `RENDER-COLORS.md`,
`NATIVE-SELECTION.md`, and `INPUT-MARKS.md` were read as history/handoffs. Actual
source takes precedence over their earlier or superseded recommendations.

## 1. Canonical node hierarchy

Notation: `G/S` = flexGrow/flexShrink, `W/H` = width/height, `p` = padding.
Omitted properties keep **OpenTUI defaults**, not guessed zeroes.

```text
Home fragment
├─ box G=1, alignItems=center, pL=pR=(terminal W<44 ? 1 : 2)
│  ├─ box G=1, minH=0                         top flexible space
│  ├─ box H=4, minH=0, S=1                  compressible offset
│  ├─ box S=0 → Logo
│  ├─ box H=1, minH=0, S=1                  compressible logo gap
│  ├─ box W=100%, maxW=75, z=1000, pT=1, S=0
│  │  └─ Prompt(disabled=global forms present, Home example placeholders)
│  └─ box G=1, minH=0                         bottom flexible space
├─ box W=100%, S=0 → Slot(home.footer)
└─ if global form: absolute box z=2000, L=R=0, B=1, pL=pR=2
   └─ box W=100% → FormPrompt

Prompt fragment (the SAME component on Home and a Session)
├─ P0 box ref=anchor, visible=props.visible!=false, W=100%
│  ├─ P1 box W=100%, border=[left], color=borderHighlight,
│  │       chars=SplitBorder with bottomLeft="╹"
│  │  └─ P2 box W=100%, G=1, S=0, pL=pR=(terminal W<44 ? 1 : 2),
│  │       pT=1, bg=raise(background.surface.offset)
│  │     ├─ optional source prompt-image strip (not part of empty milestone)
│  │     ├─ textarea W=100%, minH=1, maxH=max(6,floor(terminal H/3))
│  │     └─ metadata row: direction=row, S=0, pT=1, gap=1,
│  │          justifyContent=space-between
│  │        ├─ row G=1,S=1,minW=0,gap=1: agent / optional auto / model /
│  │        │  provider / variant; missing agent uses box H=1
│  │        └─ optional right content: row gap=1, alignItems=center
│  ├─ P3 box H=1, border=[left], color=borderHighlight,
│  │       chars=EmptyBorder with vertical=(promptBg alpha!=0 ? "╹" : " ")
│  │  └─ box H=1, border=[bottom], color=promptBg,
│  │       chars=EmptyBorder with horizontal=(alpha!=0 ? "▀" : " ")
│  └─ P4 row W=100%, justifyContent=space-between, gap=2
│     └─ prompt.footer slots: status/location, optional editor file,
│        plus built-in action/status hints
└─ Autocomplete anchored to P0 / actual editor target
```

With one unwrapped input line, no images/right content, and one metadata line,
P2 has **4 rows**: top padding + input + metadata top padding + metadata. P3 is
one additional row, then the footer. Home's wrapper pT=1 is outside that panel.
The textarea's six-row value is a **maximum floor**, not a minimum height.

Session host: row `G=1,minH=0` → column `G=1,minH=0,pB=1,pL/pR=1 or 2` → transcript
region `G=1,minH=0` → one-row jump/status strip → composer host `S=0`. The host has
the queued dock, composer-top slot, activity Composer, then conditional permission,
form, unavailable-location body, or the common Prompt. Source activity/child mode
suppresses Prompt before the other composer branches.

**Name distinction:** TS `routes/session/composer/Composer` is the activity tab panel,
not a second prompt input. Its counterpart is `SessionActivities.razor`. C#
`Layout/SessionComposer.razor` is currently a second translation of the common Prompt.

## 2. Structural map to the current application

Paths below are relative to `src/OpenCode.Cli/Tui`.

| Source boundary | Current Razor | Translation difference / dependency |
| --- | --- | --- |
| Home align/constraints | `OpenCodeApp.razor` Home Box uses `Center=true`; spacers use `Height=0`; integer `ComposerWidth` | `CrossAlignment` exists, but `Center` is not a substitute for the full source align/size contract. Percentage width, maxW=75 and minH=0 are not expressible on current Box. |
| Home prompt wrapper | Root integer-width wrapper + `Composer(Home=true)` adds pT=1 | Numeric cap approximates source width but loses declarative constraints and z=1000. Preserve exactly one outer pT after translation. |
| P1/P2 | `Composer.razor` merges border and colored/padded panel; SessionComposer repeats that merge | Source rail and surface are separate nested boxes. Current missing G=1 on the panel also avoids the engine's zero-intrinsic-grow behavior; do not blindly add it before engine correction. |
| Shared Prompt | Home uses Composer, Session uses SessionComposer; Home forms replace it with disabled SessionComposer | Source keeps the same Prompt/textarea mounted and disabled. Plain TuiText substitution loses marks/editor semantics and uses different colors. |
| P2 textarea | Both pass Value/Cursor/SelectionAnchor/TextMarks and the correct max-height formula | No source textarea ref/state API, minH, cursorStyle, focused colors, wrap prop, or disabled/visible editor semantics on current Input. |
| P2 metadata | Explicit spacer Box H=1 + fixed metadata row H=1 | Source uses row pT=1 and intrinsic text; model is `S=1,minW=0,wrap=none,truncate`. Home/Session use different nesting. Auto indicator, right content, and fade behavior are not equivalent. |
| P3 | Both draw two nonselectable TuiText runs, first now `▀`, then repeated `▀` sized from `Width-1` | Compensates for missing side/custom-border API. Source uses `╹` and `▀` border nodes, never repeated selectable text. Session also lacks source alpha==0 blank-glyph condition. |
| P4 | Separate Home/Session footer strings and fixed H=1 rows | Built-in source adds agents+commands on Home at W>=44; Home Razor omits agents. Source shell hint shortens to `shell` below W=44. Status/location has measured path truncation and branch suffix, not an unbounded raw directory. |
| Home footer | Fixed root FooterHeight of 1/3 with status/version text | Source built-in is content-sized, row gap=2, pX=2, pT/pB=0 below H=16 else 1; visible H>=12/W>=44, version W>=64. Actual MCP/plugin content controls its natural height. Do not manufacture health rows for the milestone. |
| Logo | `Wordmark.razor[.cs]` combines styled character runs | Width variants broadly follow source, but source logo glyphs are nonselectable; current wordmark text inherits selectable=true. The canonical TS logo already contains the agreed blurple dotnet badge—do not add a second badge. |
| Session host | `SessionFrame.razor` + root branch selection | Has analogous regions but fixed computed content width and no minH. Current permission/form precedence differs from source activity-first branch. Only the normal one-prompt route is milestone scope. |

## 3. Engine contract gates for a nearly literal mapping

These are concrete requirements from the tree above, not a request for a browser
layout engine or a broad rewrite. API names may differ; semantics must be explicit.

| Gate | Source requirement | Inspected bridge status |
| --- | --- | --- |
| E1: fragments/whitespace | Provider/fragment boundaries add no layout nodes. Formatting indentation is not visible text; explicit spaces/newlines inside text/editor remain data. | Renderer retains original frames; TuiNode now omits whitespace-only **Markup** from layout while preserving text/input content. Carry this forward. Do not discard frames and break Blazor sibling indices, or globally Trim text. |
| E2: size/flex | W=100%, maxW=75, minW/minH=0, min/max textarea height, G/S/basis; distinct alignItems and justifyContent, gaps/padding; absolute coordinates/z for overlays. | Box has integer W/H, G/S, cross alignment, but no percentages, min/max, basis or main-axis justification. `NaturalHeight` excludes growing children and `AllocateAxis` starts them at zero; literal P2 G=1 in an auto-height parent is not faithful. |
| E2 defaults | Column, align-items stretch, justify flex-start; grow=0. Pinned base shrink defaults to **0 if numeric W or H supplied, otherwise 1**. | Box always emits Shrink=0; its integer zero/default values cannot distinguish omitted from explicit props. `Center` only offsets the computed cross size. |
| E3: box surfaces | Box default transparent, shouldFill=true; an omitted box bg is not an instruction to repaint the parent canvas opaque. Border cells obey the same native draw semantics. | Box nullable Bg exists; parent fill and view bg are distinct after recent text fixes. Border DrawBox still receives inherited bg and fixed palettes. Compare this with source transparent Box defaults in the engine handoff. |
| E4: borders | Independent sides and all 11 custom chars, including empty chars and spaces; correct side insets, transparent fill, one-row left/bottom border behavior. | Attribute parser accepts only named styles or left. `OpenTuiNative.DrawBox` already accepts side/fill flags and 11 codepoints; missing exposure/layout mapping is not a reason to invent new FFI. |
| E5: text | Default wrap=word, selectable=true, bg transparent; explicit wrap=none + truncate; shrink/minW and inline spans preserve meaningful whitespace. | TuiText defaults wrap=Character, supports Value/Runs but no ChildContent/span tree, Truncate, minW or shrink. An explicit Value mapping is fine; manual layout Boxes are not an equivalent inline span mechanism. |
| E6: textarea | One editor/viewport owns word wrapping, caret, native word movement, selections, scroll-to-caret, text changes, and undo. Public ref operations must support set/clear/insert, selection and cursor notifications, focus/blur, submit and managed virtual marks. | Input is a controlled string/cursor/anchor paint adapter. Root reimplements movement/edit history; `MeasureInput` computes character wrapping while pinned editor defaults to word wrap. Merely renaming Input to Textarea is insufficient. |
| E6 indices | One authoritative mapping for native display offsets, UTF-16 strings and pointer positions. Marks count LF as one; UTF-8 runs are yet another representation. | Existing TerminalTextMap/metrics make units explicit. Preserve those boundaries; do not use string length for cell widths or run parallel guessed wrap/caret maps. |
| E7: events/lifecycle | Key modifiers/repeats/releases retained; preventDefault differs from stopPropagation; pointer target focus and selection happen once, before application-specific actions. Correct post-layout ref/size notification. | Rich host key route exists. Control callbacks use ConsoleKeyInfo and a combined Handled flag; pointer Handled stops bubbling/defaults. Box OnSizeChanged exists, but no editor content/cursor-change API. This semantic difference needs an explicit adapter contract. |

For E6, either expose the existing native editor through a faithful textarea ref,
or provide an equivalent controlled editor with native commands/change events.
Choose **one** owner. Do not keep a generic editor plus a second app word/selection/
undo implementation. Upstream virtual extmarks are managed wrappers, not invented
native exports. The app still owns their binding to typed prompt attachments.

## 4. Colors, corner defects, and selection classification

- Source Prompt uses `raise(background.surface.offset)` for its surface and leaves
  textarea background transparent (focused background explicitly transparent).
  Normal/focused text uses text.default or muted text.subdued; placeholder uses
  text.subdued. Cursor uses text.default, or surface.offset while disabled.
- Home ComposerTheme now uses that surface formula. SessionComposer instead uses
  TranscriptTheme.ElevatedBackground: **application translation drift**, not an
  engine default to compensate for. Home variant color currently uses formfield
  selected; source explicitly uses text.feedback.warning. Retain symbolic source
  roles in the translation review rather than relying on today's equal-looking hex.
- Source does not set selectionFg/selectionBg. Current ApplyHostTheme now omits
  those overrides, and input/text native styles use explicit Bg or transparent.
  Carry forward native per-run inversion; do not substitute formfield/action colors.
  Null/unset selection bg is not an explicitly transparent override. See the native
  owner report for the precise two-color inversion branches.

| Reported defect | Classification and current evidence |
| --- | --- |
| Extra rows from formatting | Engine render-tree/flow bug documented by emitted Razor Markup whitespace. Current source contains filtering; do not reduce textarea max height or delete meaningful text to conceal it. |
| Broad black gap at bottom-left | Earlier generated edge-row indentation consumed horizontal layout width. Filtering formatting siblings addresses that mechanism; current runtime outcome is not verified here. |
| One-cell black notch, then grey overflow around `╹` | The app substituted a text cell for native border structure, then painted its whole background grey. A background covers the whole terminal cell; scissoring cannot remove only its lower half. Current `▀` corner cap deliberately differs from source `╹` and is a workaround, not a literal fix. Restore source border calls only after E4 is handed off. Do not promise a three-color glyph cell or change page/surface colors to hide it. |
| Selection inversion mismatch | Earlier app selection-color overrides plus engine opaque inherited text bg changed native inversion. Current source removes both causes. Visible inversion still needs authorized native comparison, not a contrasting-color tweak. |
| Missing Ctrl+Shift word alias | Application default-keymap policy, not a color/layout defect. Current non-generated resolver adds aliases to existing select-word commands with user overrides taking precedence. Pinned source defaults list Alt+Shift, not Ctrl+Shift; keep the requested alias deliberately, not as a false upstream-default claim. |

No screenshot or runtime was inspected in this phase. Historical screenshots are
reported user evidence; the causal distinctions above come from source and the
existing agent reports, not new visual verification.

## 5. State operations and unavoidable framework adaptation

| Source operation | Preserve in the Razor translation / engine boundary |
| --- | --- |
| Home mount/ref | Clear editor-context selection; bind one Prompt ref; apply route prompt once; `--prompt` auto-submit waits for model readiness. Capture owner identity before async work. These are application effects, not layout defaults. |
| Content/cursor changes | Read the actual editor text, update prompt state, notify autocomplete, sync virtual marks, then update cursor version. Controlled bindings need the equivalent change ordering, without text-event/default insertion running twice. |
| `ref.set(prompt)` | Set text, full structured prompt, restore marks, move to buffer end. No text-only conversion. |
| `resetComposer()` | Clear marks, replace with empty prompt, clear mark→part map, clear editor. Source reset does not itself switch shell mode. |
| Selection | Shift keeps a fixed anchor; reversal extends from the active endpoint. Plain directional/word movement collapses to the edge. Root currently does this in Editing/Keybindings; move ownership only with E6, preserving payloads and mark identities. |
| Focus | Source blurs when hidden/disabled or dialog stack is nonempty. Otherwise focus the real editor. Source mouse-down normally asks its target to focus; it does not rebuild pointer selection in Prompt. |
| Paste/submit | Suppress default insertion before an await; normalize CRLF/CR at the boundary. Source empty bracketed paste dispatches prompt.paste. IME submission waits for content flush (source double-defer). .NET needs an explicit post-input/dispatcher boundary, not arbitrary sleeps or two independent insertions. |
| One ordinary prompt | Capture text/attachments/mode/selection synchronously, reset, create/adopt the real Session, prepare captured agent/model in source order, admit once, observe actual events. Restore the capture only into its empty originating editor on failure, never over newer input. Keep the existing typed client and shared observer; no fake assistant message. |
| Route unmount | Save the complete draft under the captured routed Session, consume its saved entry on restore, release focus/ref subscriptions and owned native resources. `@key`, OnAfterRender and disposal replace Solid ref/onMount/onCleanup semantics. |

Blazor render batches, keyed component reuse, async EventCallbacks and dispatcher
ownership are unavoidable adaptations. Raw Blazor component/region frames may be
retained internally while remaining layout-transparent. A root state/observer
adapter may remain, but it should not duplicate the Prompt's node tree. Existing
frame coalescing/explicit StateHasChanged is not equivalent to Solid reactivity by
syntax alone; engine handoff must define when ref/layout/input state is committed.

## 6. Post-handoff removal list and first-milestone gate

After the engine contract is delivered—not in this phase:

1. Map the common source Prompt once to Razor for both routes. Remove the two
   divergent prompt trees, disabled TuiText lookalike, and the H<6 Compact variant
   that strips source padding/border/metadata/edge/footer.
2. Replace manual `ComposerWidth`/`InputWidth` budget arithmetic where source uses
   W=100% + maxW/minW with those real constraints and actual laid-out measurements.
   Do not remove native offset conversions needed by controlled framework bindings.
3. Replace repeated text edge runs, `Width-1` sizing, and the fuller `▀` corner
   workaround with the exact P3 border hierarchy. Do not repaint the corner grey.
4. Replace fixed text-height clipping used instead of source wrap/truncate/shrink
   with the corresponding text props. Use source minH spacers and metadata pT;
   never encode whitespace as a layout repair.
5. Retire root pointer/word/undo duplication only when the delivered textarea owns
   those semantics. Keep the Ctrl+Shift aliases, typed attachment state, shared
   observer/admission logic, and explicit resource lifetimes.

First milestone is only Home → one editable Prompt → selection → one real prompt
and its normal Session Prompt. Compare empty/one-line/multiline/over-cap states,
widths around 22/28/44/50/70 and maxW=75, disabled/focused/muted states, transparent
and opaque prompt surfaces, exact border cells, and selection reversal/word movement.
First compare structural node/property maps; visual/runtime acceptance requires
separate explicit authorization. No broad feature parity or engine usability claim
is made by this report. **Wait for the engine handoff before application edits.**
