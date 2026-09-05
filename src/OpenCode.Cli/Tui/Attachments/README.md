# Mounted prompt references

The root mounts file/directory and agent `@` completion in both Home and Session
composers, anchored to the actual composer bounds. Search uses the authenticated
`SessionHttpClient.FindFilesAsync` at the selected server Location. File entries
retain server ranking; non-primary, non-hidden agents come only from the loaded
server agent catalog. Skills use the real typed skill catalog and picker, including
registered IDs and catalog-provided mention/slash collision handling. No skill
entries are synthesized when the catalog is unavailable.

`SessionHttpClient.Files.cs` implements the Protocol filesystem surface without
Core dependencies: list and find return `LocationResponse<IReadOnlyList<FileSystemEntry>>`;
read returns raw bytes. Location query names are `location[directory]` and
`location[workspace]`. It performs no local path existence probes or media reads.

## Selection and editing

- Enter attaches the selected file/directory or agent. Tab expands a directory
  path for further completion. Configured autocomplete bindings, Escape dismissal,
  pointer choice, loading/error states, and native-positioned popup geometry are
  used. Requests are cancelled/superseded across query, tab, and location changes.
- File line ranges use the source `#start[-end]` parser. A file URI carries `start`
  and optional `end` query parameters; directory selections ignore line ranges.
  Paths are converted according to the **server** path syntax, including drive
  and UNC paths, without resolving them through the client's filesystem.
- The editor inserts a label and a real URI/agent attachment with `PromptMention`.
  Labels are not substituted for attachments in the request. The removable file,
  directory, agent, and skill labels reflect the actual current `PromptInput`.
- Cursor/selection indexes remain UTF-16, as required by the controlled generic
  input. Mention offsets are recomputed in native display positions, counting
  newlines as one position like the source. No native display index is used to
  slice a C# string.
- The managed `TerminalTextMarks` controller now follows the installed source
  interval rules: insertion at the start shifts, insertion inside expands, and
  partial deletion trims a mark without dropping its attachment. Full coverage
  removes the mark and its bound part. Mentionless metadata is retained.
- Virtual cursor motion skips marks using the source directional/vertical rules.
  Backspace at a mark's end and Delete at its start remove its entire text. Active
  selections bypass snapping, and range deletion remains a range operation.
  Explicit attachment removal maps the cursor/selection to the remaining text.
- Undo/redo snapshots carry mark IDs, next-ID state, typed attachment bindings,
  the complete attachment document and message metadata
  by snapshot identity, so identical text with different attachments is not confused.
  Submitted prompt-history entries retain the same structured data. Native text
  metrics are required for edits involving mentions; unavailable metrics produce
  an explicit error rather than an ASCII-width estimate.

Ordinary submission now captures the observer owner's `CapturePromptAdmission`
before clearing text/attachments and uses `NetworkPromptInput`. Delivery, resume,
metadata, IDs and attachments remain in the typed protocol input. Queue submission
sets `Delivery = Queue` on that snapshot. The text-only guard remains only for hosts
that have not supplied typed transport. Fork is wired through `Client.ForkAsync`
with `ForkRequestBoundaryBefore` and restores the complete original user prompt.

The root produces `TerminalTextMark` UTF-16 paint spans using the source
`extmark.file`, `extmark.agent`, `extmark.skill`, and inline-data `extmark.paste` syntax roles. Both composers
now forward them to the actual `Input.TextMarks` paint path. The separate label
rows have been removed from the production mounts. Initial metrics come from an
input's actual completed layout, through `TerminalTextMarkMetrics`, rather than
waiting for a keyboard event or estimating widths. No native extmark FFI is
invented. Image/file content is not eagerly fetched by completion.

## Explicit rich paste

The mounted `prompt.paste` action (default Ctrl+V) calls the host clipboard reader
on the renderer dispatcher. It requests PNG, URI-list, then text representations.
It never reads the clipboard at startup. A changed tab, Session, draft, selection,
mode, client, or Location prevents a delayed result from editing a different draft.

PNG bytes become a typed data-URI file with a source-style `[Image N]` virtual
mention. URI-list file entries must be within the selected server Location and
match the authenticated typed file-list response; no local file is opened or
uploaded. Other URI entries remain text. Plain text is never parsed as a file
path. Each attachment-list insertion is one undoable edit and keeps the existing
metadata and mark bindings. Images/file attachments are rejected in shell mode.

Empty, unsupported, cancellation, timeout, limit, and failure remain distinct
feedback outcomes. The host backend's Windows/macOS file-list limitations remain
unchanged. There is no OSC52 read fallback or replacement for `ITextClipboard` copy.

Sources: original `component/prompt/autocomplete.tsx`, `prompt/parse.ts`,
`prompt/display.ts`, `prompt/codec.ts`, `component/prompt/index.tsx:810–879`, and
`packages/protocol/src/groups/fs.ts`. Verification is compile-only; no filesystem
queries, HTTP calls, clipboard operations, TUI/native launch, or tests were run.
