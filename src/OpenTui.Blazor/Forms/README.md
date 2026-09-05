# Generic terminal form state

This directory has no OpenCode schema, protocol, client, or server dependency.
It contains typed presentation definitions, synchronous validation/navigation,
and a local text editor. The consuming application's Razor components render the
state with ordinary native-backed `Box`, `TuiText`, and `Input` primitives.

- `TerminalFormDefinition.cs`: six field kinds, values, options, conditions, and
  semantic theme inputs.
- `TerminalFormState.cs`: explicit answers/defaults, ordered conditional
  visibility, choice/custom editing, external acknowledgement, and validation of
  the visible reply snapshot.
- `TerminalFormValidation.cs`: field validation and display labels.
- `TerminalFormEditor.cs`: grapheme-aware edits, selection, undo/redo, and paste.

The state never sends, cancels, opens a browser, or reads the clipboard. The
application maps its own DTOs and owns asynchronous effects and lifetime. No
`BuildRenderTree` implementation or application-specific render node is added.
