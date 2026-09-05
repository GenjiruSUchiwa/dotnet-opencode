# Consumed pixel-size responses

The host constructs TerminalInput with `consumeResponse: Func<string, bool>`
before sending its initial native `queryPixelResolution` request. The parser
offers framed non-paste responses to that callback before key decoding and
unknown-protocol rejection. Returning true consumes only that response. Paste
contents remain opaque and unrelated unsupported input retains its errors.

`TerminalPixelQuery` follows the OpenTUI 0.5.9 renderer state:

- One query may be outstanding. Further resize requests coalesce into one requery.
- A pending reply uses `ESC [ 4 ; pixel-height ; pixel-width t` with decimal
  integers. Syntax is matched over the complete frame.
- A stale reply after resize is consumed but not used; a replacement query is sent.
- Numeric overflow completes/consumes a syntactically valid reply without inventing
  geometry. Zero dimensions remain unusable for image protocol readiness.
- There is no independent query timeout or periodic retry in the inspected source,
  so this implementation adds neither. Shutdown cancels pending query state.

Both Windows and Unix can now consume these replies. Actual positive Unix
TIOCGWINSZ pixel dimensions are retained when available. Grid dimensions and
native pixel changes update the image render context; resize invalidates obsolete
query geometry. Capability updates remain independent facts. No font dimensions
or Sixel readiness are inferred from character counts.

The ABI call is the existing u32-renderer `queryPixelResolution` export declared
in OpenTuiNative.Image.cs. Source: installed zig.d.ts and renderer query,
parsePixelResolution, resize, and shutdown code in OpenTUI 0.5.9. No pixel query,
native DLL, terminal, codec, or test was executed for validation.
