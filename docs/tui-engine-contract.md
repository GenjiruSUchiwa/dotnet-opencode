# TUI engine contract — border/layout checkpoint

Scope: generic engine C# only. The two uncommitted app corner substitutions were
withdrawn to their committed state; no further CLI translation/workaround is
included. This checkpoint does **not** claim the screenshot defect is visually
resolved or that the renderer has full OpenTUI/Yoga parity.

Reference: OpenTUI 0.5.9, source identity
`df2fc1594bb7a1274fc490155305e3d9f61f1b01`, installed `zig.d.ts` and
`chunk-bun-jxfx3h5k.js`; pinned `packages/native/src/buffer.zig`.

## Invariant matrix

| Area | Source contract / current engine evidence | Checkpoint outcome |
| --- | --- | --- |
| Razor frame identity | Frame siblings must remain addressable by Blazor diffs; component boundaries are not visual boxes. | Retained Children/flattened LayoutChildren separation and formatting-only Markup filtering. Explicit text/input whitespace is preserved. No new filtering or app spacing patch. |
| Border representation | BoxRenderable.renderSelf (2699–2725) calls buffer.drawBox with side mask, custom characters, its own background and shouldFill. Prompt TSX 1925–1949 uses nested left/bottom Box borders, not text nodes. | Added typed side mask and immutable custom-character roles to Box; uses existing native bufferDrawBox. No selectable border text or per-cell overlay in the new API. |
| Border measurement | BoxRenderable.applyYogaBorders and getScissorRect use independent left/right/top/bottom insets. | Natural width/height and managed arrangement now use each selected side, instead of special-casing left versus four-sided borders. One-row bottom-only/left-only boxes can be represented directly. |
| Border fill | Native drawBoxInternal (2328–2343) fills the interior according to shouldFill; border cells are painted by the same native primitive. | Removed generic opaque prefill for bordered boxes. Native call receives local Bg or transparent, not inherited canvas color, plus the existing fill option bit. Borderless explicit fills remain supported. |
| Descendant clipping | Renderable 1144–1155 pushes child scissors only for non-visible overflow; BoxRenderable.getScissorRect (2727–2741) then excludes border cells, not padding. | Added explicit Visible/Hidden/Scroll overflow. Boxes default Visible; paint, pointer hit testing and focus visibility honor the distinction. Hidden/scroll descendants use border-inset bounds; visible descendants inherit ancestor clipping. Terminal root/modal/scroll-view limits remain bounded. |
| Native data ownership | DrawBox receives 11 u32 Unicode scalar roles synchronously. OpenTUI renderer handles are uint; RGBA8 channels occupy low bytes of ushort lanes. | Custom data is immutable at the component surface and cached as owned typed state; the existing wrapper pins only for the call. No ABI/binary/color encoding changes. |
| Text/selection | Native text views own wrapping/selection painting; unset selection overrides request per-run inversion. | Preserved. No palette swaps, glyph-background hacks or new managed selection logic. |
| Input/events/lifecycle | Source down handlers run before default autofocus; live state and callbacks have explicit ownership. | Existing ordering, aliases, marks, callback cancellation and native teardown retained. Separate source preventDefault/stopPropagation and drag/drop are still gaps. |
| General flex layout | Installed zig.d.ts 385–436 already exports native Yoga configuration, tree, style, layout and callback APIs. | Existing managed flex allocator is **not** declared equivalent. No second flex implementation was added. Follow-up should bind the existing Yoga exports rather than extend hand-written Yoga approximations. |

## New generic surface / call chain

- `Box.BorderSides`: nullable `TuiBorderSides` flags (None, Left, Bottom, Right,
  Top, All); explicit flags override legacy `Border` shorthand.
- `Box.CustomBorderChars`: `TuiBorderCharacters` with TL/TR/BL/BR, horizontal,
  vertical, four tees and cross. `Empty`/constructor defaults match source
  EmptyBorder: zero for empty strings, U+0020 for horizontal. Empty and space
  are not interchangeable. Values must be Unicode scalars (zero is the native
  empty sentinel); callers should use single-cell border glyphs.
- `Box.ShouldFill`: true by default. False is explicitly serialized as text in
  the render-tree bridge because Blazor omits false boolean element attributes.
- `Box.Overflow`: Visible by default, or Hidden/Scroll for child clipping. Scroll
  here is a clipping mode, not a second scroll controller; use the existing
  ScrollBox/TerminalScrollState for scrolling content. Painting a box and pushing
  the child scissor are separate operations, matching the source render list.
- The legacy string Border API remains supported. Its existing `left` heavy
  glyph shorthand/default inherited border color remains a compatibility path;
  new typed sides/custom borders default to source single glyphs/white border
  color unless BorderFg is supplied. BorderFg alone still does not implicitly
  enable a legacy border; translations should specify sides explicitly.

Call chain: Box parameters → typed node custom characters + side/fill attributes
→ side-derived measure/arrange/clip → existing OpenTuiNative.DrawBox
→ native interior fill and border composition. The source-required cell painter
already exists; no ABI extension, native library, graphics mechanism or package
change is needed for this checkpoint.

## Required application translation (not performed)

Replace the composer's text-based decorative edge with the source nested Box
structure: a one-row left-only border whose custom Vertical is `╹` (or space
for transparent prompt surfaces), containing a one-row bottom-only border whose
custom Horizontal is `▀` (or space). Supply borderHighlight and promptBg as the
respective BorderFg values. Leave background unset where source TSX leaves it
unset; do not assign page or prompt background to imitate glyph silhouettes.
The new API makes this translation possible without changing selection behavior.

An ordinary glyph cell still has only foreground/background colors. Native Box
composition does not invent a third subcell color. The withdrawn text-glyph
workaround and the previous full-cell grey background cannot establish source
parity. The exact terminal/font result must be compared with source once runtime
permission is granted; the current application callers are not yet translated.

## Remaining engine gaps / next boundary

The explicit handoff `docs/tsx-razor-contract.md` (E1–E7 and P0–P4) was read before
finalizing. Gate status for this checkpoint:

| Gate | Status |
| --- | --- |
| E1 frame/content identity | Existing frame-preserving whitespace/component flattening retained. No global text trim. |
| E2 size/flex/defaults | **BLOCKED.** Percent/min/max/basis, intrinsic growing children, numeric-size-dependent omitted shrink, and distinct justify-content are not implemented. The managed allocator still zeroes growing child bases. Native Yoga is the required next binding, not another app compensation. |
| E3 surfaces | Box-local transparent defaults/native fill and explicit overflow are implemented here; text/selection transparency from prior work is retained. |
| E4 borders | Side flags, all 11 custom characters including empty sentinel versus space, native paint/fill, side insets and overflow-aware clipping are implemented. No new FFI. |
| E5 text | **BLOCKED.** Existing character-wrap default, missing truncation/min-width/shrink/inline span surface remain. Do not translate source metadata using fixed-height clipping as a substitute. |
| E6 editor/offsets | **BLOCKED.** Controlled Input plus application editing is not an authoritative native textarea. Native editor ref/change/selection/undo ownership must be delivered as one owner, preserving typed marks and user key aliases. |
| E7 events/lifecycle | **PARTIAL.** Existing lifecycle/focus behavior retained and overflow visibility corrected; independent event prevention/propagation and editor change/cursor events remain missing. |

**Not ready for the canonical full P0–P4 Razor translation.** P3 can now be
expressed as real borders, but literal P2 `Grow=1` is still wrong in the current
auto-height managed layout until E2 is resolved. The application merged surface
and rail, duplicate Home/Session Prompt trees, and disabled text lookalike are
translation drift, not engine rules to preserve or hardcode. No application
translation was performed and the mapping agent should remain read-only.

Native Yoga is present in the pinned ABI, but the .NET bridge does not yet bind
its tree/style/measure-callback lifetime. Percent dimensions, full min/max/flex
basis semantics, wrapping flex layouts and absolute-layout details therefore
remain blockers to claiming almost-direct TSX layout equivalence. Yoga pointers
must not be confused with OpenTUI's uint text/renderer handles when that binding
is implemented. Native text measure targets should be reused where available.

Box titles/focused border colors, source focus/default-event distinctions, native
editor word-wrap/caret mapping, reflow hover and full drag/drop parity are not
implemented by this bounded checkpoint. Formatting-only Markup outside text is
treated as layout formatting; intentional blank layout cells require explicit
text or padding/gaps. Existing custom table tracks also remain managed behavior.

## Verification

Source/dependency inspection and pinned repository .NET 11 preview 7 build only,
with `OpenApiGenerateDocuments=false`. No tests, TUI/application, native/WASM,
clipboard, database, provider or PTY execution. Compilation is not usability or
visual verification. Final build/freeze result follows below.

**Checkpoint frozen:** repository SDK 11.0.100-preview.7.26381.103 full CLI
dependency build passed with **0 warnings and 0 errors** (18.86 seconds).
Artifacts: `C:\tmp\opencode\native-pass3-99bb7c24-6732-4b19-8b25-57b17414ae01`.
Log: `C:\tmp\opencode\tui-engine-contract-build-final.log`.
No source edits after this successful build. Application translation remains
gated by E2/E5/E6 and runtime verification remains unauthorized.

## E2 continuation — native Yoga is now the active allocator

This section supersedes the earlier E2-blocked status above. It is an
independently reviewable engine boundary, not a declaration that E6 is finished.
No application Razor, package, asset, binary or native ABI was changed.

### Implemented native path

`TuiLayoutEngine.UpdateLayout` now calls `LayoutWithYoga`. The former
NaturalWidth/NaturalHeight/AllocateAxis/Layout/absolute-placement helpers and their
intrinsic-width cache were removed. There is no fallback generic flex allocator.

Call chain:

1. Flattened, frame-preserving TuiNode tree → one NativeYogaTree transaction.
2. Yoga config uses source `useWebDefaults=false` and point scale 1.
3. Styles map to existing yogaNodeStyleSetEnum/Float/Value/Border exports.
4. Text/code leaves attach the existing native renderable measurement target to
   their live TextBufferView. Yoga → native renderable → native view measurement
   stays native, rather than guessing Unicode widths or duplicating wrapping.
5. yogaNodeCalculateLayout → six-f32 computed layouts → scene coordinates.
6. Measurement owners detach, all child links are removed, nodes are freed, then
   config is freed before UpdateLayout returns. Native text views remain owned
   by the existing scene nodes for painting and selection.

The per-transaction tree intentionally trades retained Yoga caching for simple,
explicit removal/reparenting lifetime: no pointer from an old render batch remains
in the next layout. Performance has not been measured. Node/config values are
pointer-sized nint; native renderable/text-view values are uint handles. Computed
layout is a 24-byte output struct. All imports are source-generated LibraryImport.

For managed measurement, the existing C callback signature is
`(node pointer, f32 width, u32 widthMode, f32 height, u32 heightMode) -> void`,
with yogaStoreMeasureResult supplying two f32 results through native TLS. One
UnmanagedCallersOnly Cdecl trampoline is installed only during synchronous
calculation, under a shared calculation gate, and cleared on exit. Per-tree
delegates remain strongly rooted. Exceptions are captured and rethrown after
native calculation, never across the C frame; mutation, disposal and recursive
calculation from a measure callback are rejected. Pointer membership, parentage,
cycles, insertion indices and measured-leaf restrictions are checked before
calling Yoga. Views must remain dispatcher-owned/live through the transaction.

ABI evidence: pinned native yoga.zig enums 11–82, layouts/callbacks 84–111,
configuration 175–185, setters 419–569 and callbacks 592–650; installed zig.d.ts
385–436 and nativeRenderable exports 722–725. Existing native-renderable measure
targets distinguish TextBufferView (1) and EditorView (2); this transaction uses
the former for actual text/code leaves. No new exported function was invented.

### Generic property contract

Existing integer Width/Height/Grow callers remain valid. The common
LayoutComponentBase on Box, TuiText, TuiCode and legacy Input adds:

- WidthValue/HeightValue: TuiLength cells, percent, auto or undefined; these take
  precedence over legacy integer dimensions.
- MinWidth/MinHeight/MaxWidth/MaxHeightValue and FlexBasis (typed TuiLength).
- FlexGrow/FlexShrink (fractional weights) and AlignSelf.

Box additionally exposes distinct JustifyContent, AlignItems, AlignContent and
FlexWrap. Row/column reverse directions are supported. CrossAlignment and Center
remain compatibility shorthands, overridden by explicit AlignItems. Existing
Box.Shrink assignments still work; omitted Shrink is now nullable and resolves
to source shrink=0 for numeric Width/Height, otherwise 1. Explicit shrink wins.
Source intrinsic growing children are measured by Yoga; Grow no longer forces
their intrinsic basis to zero. Integer padding/gaps/borders/absolute edges are
passed through to Yoga, not apportioned by another layout loop.

### Root, scroll, modal and table integration

- Terminal root supplies its actual width/height to Yoga. E1 formatting filtering
  and E3/E4 native border/overflow behavior remain intact.
- ScrollBox has a constrained viewport and a synthetic auto-height, non-shrinking
  Yoga content node. Yoga supplies row starts/heights and content extent;
  TerminalScrollState supplies only retained-anchor/bottom-follow translation.
  Absolute children stay attached to the viewport. This is not a second row-height
  allocator; horizontal-scroll UI remains outside the existing scroll-state API.
- Modal portals calculate independent Yoga roots. Existing default width cap,
  quarter-screen/center placement policy remains explicit overlay policy, while
  Yoga determines panel size and child layout. It is not claimed to be the full
  source dialog component API.
- SharedColumns remains an explicit table extension, not a hidden flex branch.
  Cell intrinsic widths come from Yoga probes with native text measurement. The
  pinned TextTable proportional fitter/expansion policy (index.bun.js
  10595–10669, 11198–11215) produces shared widths; Yoga then recalculates actual
  cells, rows and containers. Parent tables precede nested ones. The old fair-share
  custom allocator was removed. This does not turn the existing Razor table tree
  into every feature of source TextTable.

### E5 boundary delivered; E6 remains required

TuiText and TuiCode now default to native Word wrapping, expose native Truncate,
and accept min-width/shrink/size constraints. NativeTextView.SetTruncate binds the
existing textBufferViewSetTruncate export. The nonselectable one-row direct-text
paint bypass was removed: word-wrap, truncation, styled runs and selection now
share the same measured native view rather than clipping a separately drawn line.
Value/Runs remain supported; a JSX-like arbitrary inline ChildContent/span tree
is not yet a public TuiText API.

**E6 is not complete.** Input remains the controlled editor with existing app
word/selection/undo ownership. Its Yoga measure callback uses native intrinsic
width but retains the existing single caret/character-wrap map for row height,
including the full-width end-of-line caret row. Removing that row or switching
Input to Word wrapping before replacing the editor owner would make layout and
pointer/caret positions disagree. This legacy adapter is explicit, not advertised
as source Textarea or a second general layout algorithm.

Next independently scoped E6 work must bind the already-exported EditBuffer /
EditorView into one dispatcher-owned textarea state, attach native measure target
kind 2, expose content/cursor/change/focus/edit operations, and route native word
movement/selection/undo exactly once. Then retire Input's manual caret map and
the app's duplicate editor operations through a coordinated adapter handoff.
Preserve user key aliases and typed marks/attachments; no app changes were made
prematurely here. E7 independent prevention/propagation and editor notifications
remain coupled to that delivery. Nearly literal P0–P4 translation is still gated
by this ownership boundary and authorized visual/runtime verification.

### Verification

Source inspection and pinned compiler builds only. No Yoga/native/WASM, parser,
TUI/application, tests/samples, PTY, clipboard, database or provider execution.
Config/struct/callback signatures were checked statically; their actual loading,
float/grid rounding, font-specific rendering and teardown behavior remain
unverified. Final compilation/freeze result follows below.

**Native Yoga checkpoint frozen:** after the interrupted build was rerun, the
full CLI dependency build succeeded with **0 warnings and 0 errors** (11.55
seconds). Repository SDK: 11.0.100-preview.7.26381.103. Command:

```powershell
.\.dotnet\dotnet.exe build src\OpenCode.Cli\OpenCode.Cli.csproj `
  --artifacts-path C:\tmp\opencode\native-yoga-e2 `
  -p:OpenApiGenerateDocuments=false -v minimal
```

Log: `C:\tmp\opencode\native-yoga-e2-build-final.log`. The removal audit found
no remaining NaturalWidth/NaturalHeight/AllocateAxis/SharedColumnWidths/
LayoutAbsoluteChildren/IntrinsicTextWidth helpers. No C# source changes followed
the successful final build. This checkpoint supplies the active Yoga allocator
and E5 native text properties; E6 and the explicitly listed API/runtime gaps
remain required before claiming the complete Prompt translation is ready.
