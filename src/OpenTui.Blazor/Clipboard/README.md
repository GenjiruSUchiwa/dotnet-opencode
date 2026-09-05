# Rich host clipboard handoff

## Explicit Ctrl+V producer boundary

The root owns binding Ctrl+V and deciding whether returned content becomes text,
an image attachment, or file references. No automatic binding, startup read, or
background clipboard-content read is installed by this batch.

On the renderer dispatcher, from that explicit user action:

```csharp
var result = await host.ReadClipboardAsync(
    new ClipboardReadRequest(["image/png", "text/plain"]), cancellationToken);
```

That preference order matches `packages/tui/src/clipboard.ts`. When file-list
support is explicitly wanted, add `text/uri-list` at the desired preference
position; its availability is platform/provider-dependent.

- `Read`: use `Representation.MimeType` and its copied `Bytes`.
- `image/png`: the native backend supplies actual encoded PNG bytes. Convert
  those bytes to base64 only at the app's prompt-attachment boundary.
- `text/plain`: `ReadText()` decodes the owned UTF-8 representation.
- `text/uri-list`: `ReadUris()` parses URI-list lines/comments; `ReadFiles()`
  returns file URI/path references only. It never guesses file paths from plain
  text, opens those files, or admits them into a prompt. The root must apply its
  real file/location policy before using references.
- `Empty`, `Unsupported`, and `Cancelled` are distinct non-read outcomes.
- `TimedOut`, `LimitExceeded`, and `Failed` must be reported as such, not treated
  as an empty clipboard. Failure includes a diagnostic/native error code when
  the native operation supplies them. Invalid requests/lifetime errors can throw.

The old `ITextClipboard` contract and implementations are unchanged. Hosts may
inject an `IClipboardReader` using the new optional constructor parameter, or
provide an `IRichClipboard` implementing both APIs. Otherwise the host lazily
creates `NativeHostClipboard` on the first explicit read. Host disposal owns only
that internally created reader; injected services remain caller-owned.

`NativeHostClipboard` additionally supports typed host write/clear results. Its
ITextClipboard adapter throws when a write did not succeed, preserving the old
callers' Task success contract. Existing default Windows text writing is not
silently replaced.

## Native requests and lifetime

`NativeClipboardService` uses only the inspected OpenTUI 0.5.9 clipboard service
and operation exports. MIME preference requests use the source little-endian
u32 count and length-prefixed UTF-8 MIME essences. Preferences are normalized to
lowercase, preserve order, reject parameters/invalid tokens, and are limited to
64 entries of at most 255 ASCII bytes each.

Source defaults are retained: 1000 ms timeout; 8 MiB read and write budgets;
64 * 1024 * 1024 image pixels; 512 MiB conversion budget; 16 native
operations and provider transfers. Native deadlines govern results. Cancellation
requests native cancellation and waits for terminal status and destroy readiness;
it never frees an active worker or exposes borrowed result memory.

Native calls are serialized. Pending operations are polled at the source 1 ms
cadence. After an operation, native provider-transfer work is drained at 8 ms
while needed, preserving X11/Wayland ownership of user-written clipboard data.
This is serving an existing owned selection, not an automatic clipboard read.
Shutdown cancels outstanding operations, retires them, waits for native shutdown
readiness, then destroys the service. The native service itself is also lazy;
constructing the managed service does not load native code or inspect clipboard
contents.

MIME, data, and diagnostic lengths/copy statuses are checked. Result bytes are
copied before operation destruction. There are no native pointer-valued results.
Data exceeding the read/managed-array bound is a limit result, never truncated
and presented as successful content.

## Platform and conversion limits

The inspected native Windows backend reads CF_UNICODETEXT and image/png, trying
registered PNG, DIBV5, then DIB. Its existing bounded native converter supplies
PNG when conversion is required. The macOS backend supports text/plain and
image/png. Native Linux uses its Wayland/X11 offer/transfer machinery and can
return matching URI-list representations where offered.

Windows CF_HDROP/file lists and macOS file-URL reads are not implemented by those
inspected native backends. This wrapper does not claim otherwise. A URI-list-only
request can return Unsupported there. A mixed request can return native Empty
when none of its supported requested representations is available; this does
not assert that every possible OS clipboard format is absent.

Headless/missing native backends, unavailable primary selections, and native
system errors retain their actual statuses. No PowerShell/Bun/helper process,
WPF, extra graphics engine, or fallback file-path inference is used. Optional
`Representation.DecodeImage()` returns an owned existing NativeImage handle for
presentation/inspection; it is not an invented PNG byte exporter or automatic
Core normalization. Clipboard conversion budgets and NativeImage decode budgets
are separate source contracts.

## Terminal clipboard policy

Source clipboard reads call the host backend only. There is no OSC52 read-query
fallback in this API, so no new parser response consumer or generic unknown-input
suppression is installed. Source OSC52 write policies distinguish attempted
terminal delivery from confirmed host writes; this first rich-read batch does
not claim to implement that composite write policy.

## Provenance and validation

Inspected installed OpenTUI 0.5.9 `zig.d.ts`, `lib/clipboard.d.ts`, and
`host-clipboard.native/internal` production chunks, plus native `lib.zig` and
`clipboard/host.zig`, Windows/macOS and Linux backend production paths.
Bindings use u32 service/operation handles, u8 status/selection values, explicit
byte lengths and source-generated LibraryImport declarations.

Only full pinned .NET 11 CLI build attempts were used for validation. No
clipboard operation, image codec, native DLL, terminal protocol query, test,
network/API, database, or process command was executed for verification.
