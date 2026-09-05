# Native image presentation handoff

## Mount status and root API

The image state/render path and Razor components are implemented. This subtree
does not edit the root app or claim that the preview dialog/thumbnail strip is
mounted. Create a host-owned loader with real source access:

```csharp
var loader = new ImageSourceLoader(new ImageSourceAccess(
    OpenMedia: OpenAuthorizedMediaStream,
    AuthorizeFile: AuthorizeLocalImage,
    AuthorizeHttp: AuthorizeRemoteImage,
    BaseDirectory: SelectedLocalDirectory));
```

Each delegate is optional, but an absent file/HTTP delegate **denies** that access.
The media delegate may resolve authenticated server/media resources and returns
an owned stream, or null when it does not handle that source. It is tried before
local file/HTTP routing. It must enforce the real location/permission policy and
observe cancellation. Do not use a callback that returns placeholder image bytes.

Data URIs need no I/O backend. `new ImageSourceLoader(new ImageSourceAccess())`
therefore supports admitted inline image bytes without enabling file or network
access. The HTTP implementation is anonymous, disables automatic redirects and
cookies, authorizes every redirect target, checks status/content length, and
enforces the encoded-byte limit while streaming. Server credentials are never
copied into it; use the authorized media backend for those resources.

For a preview request, create a fresh controller and render:

```razor
<DialogImagePreview @key="PreviewController" Controller="PreviewController"
                    Theme="ElevatedThemeTokens" TerminalHeight="_height"
                    OnClose="CloseDialog" />
```

`new ImagePreviewController(loader, request.Images, request.Initial)` clamps the
initial index. The dialog owns/disposes this controller, cancels stale loads,
and waits for its pending reads before releasing decoded image state. Keep the
loader alive until all its dialogs/thumbnails have been disposed.

The view follows `component/dialog-image-preview.tsx`: centered extra-large
modal, two-cell content padding, `Image n of total`, height `max(3, rows - 8)`,
fit mode, left/right cycling, click navigation, escaped close, mention caption,
and `No preview` on failure. Previous decoded content is retained while a new
source loads, as in the source ImageRenderable; loading/failure is not reported
as successful decoding of the new URI.

## Transcript hooks

- Bind `SessionTranscript.OnImagePreview` to the root preview-opening handler.
  It carries `ImagePreviewRequest` and is forwarded through Markdown rendering.
  Standalone Markdown image references get a preview click action only when
  that handler is connected. Selection releases do not activate the action.
- `MarkdownImageReferences.Read(document/inline)` returns actual AST image
  references for a root-owned gallery command. It never fetches them. Mixed
  prose/image spans are not assigned guessed hit coordinates.
- Rendered Markdown keeps the source link-label behavior (empty alt text becomes
  `image`); source mode retains the original Markdown text.
- `TranscriptImages.User` builds data URIs from admitted attachment bytes and
  applies the source's inline/mentioned-image deduplication rule.
- `TranscriptImages.Tool` includes only file content with an image MIME type and
  `data:image/` URI, matching `inlineToolImages` in the session route.
- `ImageStrip` implements the optional session thumbnails: up to three cover
  previews, height `max(4, min(8, rows / 4))`, double-width tiles, and `+n more`.
  Bind `Enabled` to the real `session.image_preview` preference (default false),
  supply the loader/theme, and handle `OnPreview`. No root setting is registered
  here before its consumer is mounted.

## Rendering, capabilities, and ownership

Generic `TuiImage` accepts caller-owned `ImageState`, fit/fill/cover, dimensions,
and protocol. The generic library performs no source access and has no OpenCode
dependency. Native decoding, cropping/resizing, ICC conversion, pixel copying,
and `bufferDrawImage` use the existing OpenTUI engine. No alternate image engine,
WPF, third-party graphics DLL, or ASCII-art renderer was added.

The render context selects Kitty only with reported Kitty graphics capability.
Sixel additionally requires actual pixel dimensions. Auto respects the reported
protocol/multiplexer policy, but does **not** fall back to blocks in this port,
per the task restriction. Unavailable protocol or rejected placement produces
`RenderError`/`No preview`, not a fake successful image. A native accepted
placement is not a claim of acknowledged terminal delivery.

Cell aspect ratio and fit/cover crop math follow ImageRenderable. Windows pixel
queries use the native query entry point and recognize replies only while a
query is pending, coalescing resize re-queries. The parser now has an explicit
consumed-response path, so Unix query replies are supported without suppressing
unrelated input errors. Actual positive Unix TIOCGWINSZ pixels are preserved.
See `OpenTui.Blazor/PIXEL-RESPONSES.md` for the query lifecycle.

Every NativeImage owns a native image handle and an ICC-cache lease. Retain uses
the native reference-counted image API, not an alias of a managed handle. The host
holds a cache lease through renderer/buffer destruction so retained lazy frame
placements cannot outlive the cache ownership. Model disposal releases its own
handle; native buffer placement lifetimes remain native-owned.

Verified ABI: eight u32 Info fields (32 bytes); i32 x/y plus nine u32 draw fields
(44 bytes); u32 handles/status codes; explicit byte spans/lengths; native limits
of 64 MiB encoded, 16384 per dimension, 25 million pixels and 100 MiB decoded.
Statuses 0–11 retain their source meanings. Mutable `imageGetPixelsPtr` is not
exposed: managed callers use the verified `imageCopyPixels` API, which avoids
borrowed mutable pointers and shared-image mutation constraints.

Sources: installed `@opentui/core@0.5.9` zig.d.ts, image/renderer chunk source,
native `lib.zig` and `image.zig`, `component/dialog-image-preview.tsx`,
`routes/session/index.tsx` SessionImages, and `prompt/attachment.ts`.

## Separate preparation boundary

Presentation is not prompt admission or normalization. NativeImage exposes
Inspect, Resize, Extract, CopyPixels and EnsureEncodedPng for a future host
adapter. EnsureEncodedPng only ensures native-owned encoding; this inspected ABI
has no encoded-PNG byte getter, so no invented `ToPng`/normalized-byte result is
returned. A real preparation/export contract must be designed separately before
feeding normalized media into Core. No Core/Server TUI dependency was added.

Verification is source inspection and full pinned .NET 11 builds only. No image
codec, native DLL, renderer, clipboard, network/API, user file, or runtime probe
was executed for validation.
