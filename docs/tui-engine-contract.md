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
