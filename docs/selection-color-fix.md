# Text selection color correction

## Root causes and implemented changes

1. `OpenCodeApp.Theme.cs` supplied formfield-selected foreground/background as
   global text-selection overrides. Those states describe selected controls, not
   a selected range of text. Upstream TUI supplies no selectionFg/selectionBg on
   its prompt or transcript. Host overrides are now omitted (null), preserving
   native per-run selection inversion.
2. `TuiLayoutEngine` passed the inherited opaque canvas background as the native
   text/textarea default. Upstream text views default to transparent, composing
   over their already-painted parent. Native defaults now use only an explicit
   view Bg or transparent. Parent box painting and explicit view backgrounds
   remain unchanged.
3. Home `ComposerTheme.InputText` now follows upstream prompt text.default /
   text.subdued rather than formfield.default / disabled. Built-in values usually
   agree, but custom themes can intentionally distinguish these roles.

The path is production theme resolution → ApplyHostTheme → existing
InteractiveTui ApplyRenderColors callback → host.Colors / Renderer.Colors →
view-local-or-host selection overrides → NativeTextView selection APIs. No
InteractiveTui wiring change is required. Local explicit selection overrides
retain precedence. Null is not the same as an explicit transparent selection Bg.

## Exact native behavior and expected colors

Pinned source `packages/native/src/buffer.zig` at
`df2fc1594bb7a1274fc490155305e3d9f61f1b01`, lines 1939–1947:

- With explicit selection background S: selected background is S; explicit
  selection foreground wins if provided, otherwise the run foreground remains.
- Without a selection background: selected background is the run foreground F;
  selected foreground is the run background B when B has nonzero alpha, otherwise
  opaque black (`#000000`). This is the native fallback, not a chosen theme color.
- A foreground-only override does not bypass the native inversion branch.

For the shipped `opencode.json` theme after its existing V1 migration, transparent
views therefore have these expected unmodified-text selections:

| Run | Dark selected foreground / background | Light selected foreground / background |
| --- | --- | --- |
| Plain text and normal prompt text | `#000000` / `#eeeeee` | `#000000` / `#1a1a1a` |
| Error-colored text | `#000000` / `#e06c75` | `#000000` / `#d1383d` |
| Subdued text | `#000000` / `#808080` | `#000000` / `#8a8a8a` |

Syntax/marked spans invert their own resolved run colors, not a common action or
feedback selection palette. Explicitly opaque view/span backgrounds become the
selected foreground. Custom and fallback V2 themes obey the same rule using their
resolved text/background values; no new token or hardcoded interactive hue is
introduced. Formfield/action selected states remain unchanged for actual controls.

Conversion review: ThemeColor.Native and generic hex parsing retain RGBA8 in the
low bytes of four ushort lanes. Native selection wrappers pass background then
foreground with null pointers for absent overrides. No channel swap, alpha
rescaling, or `*257` conversion change was needed.

## Production caller integrated

With explicit ownership of this one attribute after the keyboard agent froze,
Home `Components/Composer.razor` now omits the Input Bg override. Its enclosing
Box still paints `Colors.PromptBackground`; the native text view uses its
transparent default, matching upstream prompt textarea in focused state too.
All pointer, keyboard, paste, text-input and text-mark handlers are unchanged.
Home now follows the same native selection rule as SessionComposer, which already
omits Input Bg. No production caller change remains outstanding for this fix.

Preserved: TuiText.Selectable default, Home pointer handlers, QuickEdit disabling,
input parsing/keybindings, native ABI, and existing renderer ownership.

## Verification boundary

Source: installed OpenTUI 0.5.9 TextBufferRenderable defaults (2799–2837),
EditBufferRenderable defaults (5538–5570), and current upstream prompt text colors
(1787–1792) / focused transparent background (1866). Light/dark defaults and custom
fallback token definitions were inspected from tracked sources, not user configs.

No tests or runtime/native rendering were executed. Parent visual verification
should compare Home and session prompt selections, plain/error/syntax transcript
selections, theme switching while selected, and custom themes with distinct
formfield.selected and text.default values. In light mode, verify the native
black-on-dark-text inversion against upstream rather than inventing a contrasting
white foreground. Final build result is recorded below.

**Frozen:** full CLI dependency build succeeded with **0 warnings and 0 errors**
using repository SDK 11.0.100-preview.7.26381.103 and
`OpenApiGenerateDocuments=false` (1:36.37). Existing incremental artifact path:
`C:\tmp\opencode\native-pass3-99bb7c24-6732-4b19-8b25-57b17414ae01`.
Log: `C:\tmp\opencode\selection-colors-build.log`. No runtime verification was
performed and no further C# source edits followed this build.

## Follow-up: Home black edge gap and excessive composer height

The user supplied a screenshot description: a partial black strip at the lower
left of an otherwise grey Home composer, plus excessive blank rows around one
short input line. The red rectangle is an annotation and is not rendered here.

### Confirmed structural cause

A permitted compiler-only build emitted Razor source to
`C:\tmp\opencode\composer-layout-generated` (no generated file was edited).
`Tui/Components/Composer_razor.g.cs` contained:

- `AddMarkupContent(13, "\n        ")` immediately after Attachments and before
  Input (line 217 in that emitted snapshot).
- `AddMarkupContent(32, "\n            ")` between the intentional one-row spacer
  and the model metadata row (line 503).
- `AddMarkupContent(91, "\n            ")` between the `╹` and `▀` text components
  in the bottom edge (line 1383).

The renderer inserted those frames as ordinary #text nodes. In column layout,
the newline-and-indent nodes contributed extra wrapped text rows: the two Home
panel nodes above account for four unintended rows. In the horizontal bottom
edge, NaturalWidth measured the indentation as **12 columns**. Those columns
consumed row width between the one-cell left edge and the half-block run, leaving
the page background exposed and clipping the run on the right. This directly
explains a localized left black gap; a native clipping bug or grey overlay is not
needed to explain it. SessionComposer's emitted source contains equivalent
formatting-only markup nodes.

`TuiRenderer` now records whether a raw text frame came from Markup. TuiNode keeps
every original child frame so Blazor sibling indices and future diffs remain
correct, but excludes whitespace-only Markup frames from layout/paint children.
TextContent updates invalidate this classification, including transitions between
formatting and visible markup. Text/input content still reads its original
children verbatim; explicit text-frame whitespace and TuiText values, including
spaces/newlines, are preserved. Authors needing intentional layout spacing use
Box padding/gaps or explicit text, not formatting-only markup between components.

### Decoration and native compositing

Upstream prompt bottom edges (prompt/index.tsx 1925–1949) are one-row Box borders:
left `╹` plus bottom `▀`, not selectable text content. Only the port's two
decorative TuiText edge runs now set Selectable=false. This both matches their
border role and prevents a text drag from inverting half-blocks. The recent global
TuiText selectable default stays true. All real input/transcript selection paths,
syntax backgrounds, low-byte RGBA encoding and native inversion remain intact.

The native source checks the viewport/scissor before writing glyphs; transparent
text composes over the already-painted canvas. This pass does not replace that
path, paint an opaque grey strip over it, or enlarge clipping rectangles. The
prompt Box still owns its grey background. The bottom edge keeps page background
below the grey upper half-block, as the source border does. Input's redundant
opaque Bg was removed as authorized; its enclosing Box background is unchanged.

### Height rules and coverage

Upstream prompt/index.tsx 1694, 1711–1716, 1793–1794 and 1870 specify:

- textarea minimum height **1 row**;
- maximum height **max(6, floor(terminal rows / 3))**;
- horizontal padding 1 below terminal width 44, otherwise 2;
- one top padding row and one padding row before metadata;
- one bottom decoration row and the separate footer row.

The port already used the correct positive-integer max-height formula. Generic
Input defaults to one row for ordinary fields, measures at least one input line,
has no grow weight, and caps its natural line count at MaxHeight. The bug was not
that max height was a six-row minimum: structural whitespace inflated its parent.
No hardcoded pixel/row subtraction or smaller max height was added.

With no attachments and ordinary metadata visible, Home's grey panel now measures
input rows + 3: one top row, input, one metadata spacer, one metadata row. A short
line therefore uses **4 grey panel rows**, followed by the existing half-block
edge and directory/footer. The separate Home outer spacing remains outside the
grey panel. Existing Session layout also loses its accidental whitespace siblings.

The disabled/form-state SessionComposer branch used unbounded TuiText rather than
the capped editable Input. It now uses an optional TuiText natural-height cap with
the same source max-height formula. This leaves it non-editing/non-focusable and
preserves its existing subdued text and value. TuiText's default remains uncapped;
explicit fixed Height still wins. Other form inputs retain their own existing
one-row or explicitly configured limits. The cap is managed layout only, not a
new native feature.

Narrow/wide available width still comes from the real border and padding budget,
now without indentation nodes consuming width. Wrapped/multiline editable input
still grows to the existing source cap, and the native viewport/caret scrolling
path is unchanged. No key mapping, word-selection algorithm, text mark, metadata,
queue/steer logic, or provider transport was edited. The existing character-cell
wrap/caret mapper was not replaced by a new word-wrap algorithm in this fix.

### Remaining visual confirmation

Runtime permission is still pending. Compare one-line, wrapped, multiline and
over-cap input at narrow/wide widths, Home and existing sessions, empty/disabled
form states, and attachments. Confirm the full-width half-block edge has no
12-column black notch and cannot be text-selected, that the prompt retains its
grey canvas, and that real selected/syntax/error text still uses the corrected
native color semantics. These are source-derived expected results, not executed
visual tests. Final integrated build checkpoint follows below.

**Integrated pass frozen:** full CLI dependency build succeeded with **0 warnings
and 0 errors** (28.05 seconds), using repository SDK
11.0.100-preview.7.26381.103, `OpenApiGenerateDocuments=false`, and the same isolated
incremental artifact directory above. Log:
`C:\tmp\opencode\composer-selection-layout-build-final.log`.
The separate compiler-output inspection build also passed cleanly; its emitted
Razor sources stayed under `C:\tmp\opencode` and were only read, never edited or
executed. No tests or runtime probes ran. No source changes followed the final
successful build; production caller integration is complete.
