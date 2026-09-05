# Native Code primitive

`TuiCode` renders through the existing NativeTextView with an attached
NativeSyntaxStyle. It accepts Content, Filetype, SyntaxRules, Highlighter,
Conceal, Streaming, DrawUnstyledText, BaseHighlight, and native wrapping/colors.

`ICodeHighlighter` is a real producer boundary, not a fake implementation. It
receives the original content/filetype/revision and a cancellation token and
returns HasParser plus capture ranges. Captures use UTF-16 source offsets with
injection/conceal metadata. `TuiCode` now defaults to the real, lazy
`TreeSitterHighlighter` shared across mounted code views. An injected producer
still overrides it. Unsupported languages stay plain with HasParser=false and a
diagnostic; no regex lexer is used. See `TREE-SITTER.md` for supported assets and
the build-only verification boundary.

`CodeHighlightState` keeps one highlighting loop, coalesces newer snapshots,
cancels stale work, rejects stale results, and preserves the source's streaming
display policy. Errors fall back to current plain content. `HighlightingDone`
allows owners to await completion; disposal cancels and awaits the producer.

`CodeProjection` implements capture-driven scope specificity, first-scope
fallback, injection suppression, conceal replacements, and source-line mapping.
It never discovers tokens from the text. NativeSyntaxRule.AttributeMask expresses
which attribute bits an overriding scope actually specifies; the theme adapter
uses the existing generator's positive flags without inventing clears.

Native ranges are not parser offsets. The renderer maps projected UTF-16 spans
to whole text elements and **line-local native display columns** using live native
cell widths, coalesces each line span, and applies actual native highlights.
The separate AddDisplayHighlight API follows the native cumulative-display
convention excluding newlines. Both APIs retain the 16-byte ExternalHighlight
layout and u32 style IDs/u8 priority/u16 references.

NativeSyntaxStyle.Register/Resolve/Count call the real native style registry.
Views retain leases; caller disposal cannot destroy an attached style. Replacement
detaches/releases the old style and view destruction precedes final style release.
The native add-highlight ABI is void and can drop native allocation failures;
HighlightCount is exposed, not replaced with an invented success acknowledgement.

## Transcript handoff

`TranscriptSyntax.Rules(themeTokens)` adapts `ThemeSyntax.GenerateNative` with its
declaration/scope order. Pass that list and a real `ICodeHighlighter` to
SessionTranscript.SyntaxRules and SessionTranscript.CodeHighlighter. Fenced code
forwards language, streaming state and raw code content to TuiCode; code-copy
continues to copy the original fence contents. The root app was not edited.

Wasmtime 44.0.0 and the pinned upstream WASM assets are now bundled. The original
PARSER-PROPOSAL.md is historical. Compilation is not a claim of runtime verification
or full language coverage/parity.
