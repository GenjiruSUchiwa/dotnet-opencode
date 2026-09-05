# Host-resolved render colors

Set `OpenTuiHost.Colors` or `TuiRenderer.Colors` on the UI dispatcher when the
application's resolved theme changes:

```csharp
host.Colors = new TerminalRenderColors(
    foreground, background,
    Cursor: cursor,
    SelectionForeground: selectionForeground,
    SelectionBackground: selectionBackground);
```

Values are typed `NativeRgba` colors supplied by the caller's semantic roles.
The library does not inspect OpenCode themes or invent a light palette. Updating
colors marks the renderer dirty, updates root inherited colors, and changes the
native frame-buffer clear color. The old `(10,10,10)` is retained only as the
compatibility default, not an unconditional frame clear.

`Input` also accepts typed per-control `CursorColor`, `SelectionForeground`, and
`SelectionBackground`. Omitted values inherit the host setting. A host cursor
color omitted entirely falls back to the generic foreground; selection without
either color retains the previous inverse-video compatibility behavior. Supplied
selection colors are sent directly to native text drawing, without inversion.

Cursor color uses a new partial binding in
`OpenTui.Native/OpenTuiNative.Cursor.cs`: `setCursorColor(uint renderer, RGBA*)`.
This follows OpenTUI's renderer-scoped color export and the existing packed RGBA
lanes; it does not emit ANSI from Blazor or introduce a software cursor. Existing
native files were not modified. Native symbol/runtime behavior was not probed.

Applications must use their actual cursor/selection role values. Do not substitute
error/success/action colors merely because they look similar.
