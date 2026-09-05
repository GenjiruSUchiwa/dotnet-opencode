# Native WebFetch

`Builtins.WebFetchTool` implements the current `tool/plugin/webfetch.ts` leaf with
an actual `HttpClient` transport, bounded body collection, and parser-driven HTML
conversion. It requires both `WebFetchTransport` and `IToolPermission`; the old
unauthorized `WebFetchTool(HttpClient)` prototype constructor is gone.

## Opt-In Composition

Create a host-owned `WebFetchTransport` and pass it as `LocalToolOptions.WebFetch`.
Only then does `ToolLocationFactory` add webfetch to its stable builtin transform.
The transport is safe to share across Locations and must be disposed by the host
after active Location requests settle. No provider-authenticated HttpClient is
accepted or reused. Null leaves webfetch unregistered. Shell remains separate.

This change does not alter Server/runner tool advertising or activate all tools.
The existing registry codec and snapshot lifecycle validate input/output and retain
leaf authorization. Session still owns result persistence and generic output storage.

## HTTP Contract

- Input is `{ url, format?, timeout? }`. Format defaults to markdown; text and html
  are also accepted. Timeout is finite, greater than zero and at most 120 seconds;
  its default is 30 seconds. It covers HTTP requests, redirects, challenge retry,
  headers and body collection, not approval waiting or synchronous conversion.
- Absolute HTTP(S) URLs are required. Credential-bearing URLs are explicitly
  rejected. No Authorization, cookies, default credentials or client certificates
  are configured. Errors do not echo URLs, transport exception text or secret headers.
- Permission action is `webfetch`, resources are the original URL, save is `*`, and
  metadata is the decoded input. Source IDs come only from the canonical ToolContext.
  Policy blocks and correction feedback are explicit recoverable leaf failures.
  User declines and caller cancellation retain their control semantics.
- Requests use the source OpenCode user agent, exact format-specific Accept headers
  and `Accept-Language: en-US,en;q=0.9`. No page scripts or subresources are loaded.
- GET follows 301/302/303/307/308 redirects, up to 20, including cross-origin and
  HTTP/HTTPS changes. Only HTTP(S), credential-free redirect destinations are accepted.
  Like the source leaf/default fetch transport, approval concerns the original URL;
  this is not a per-hop permission system, DNS/private-address filter or SSRF sandbox.
- A 403 with `cf-mitigated: challenge` causes exactly one retry with user agent
  `opencode`. A remaining challenge is explicitly unsupported. There is no browser,
  CAPTCHA solving, cookie replay, login flow or fabricated successful response.
- Only 2xx statuses succeed. The transport decompresses supported HTTP encodings.
  A known valid declared length over 5 MiB is rejected; the stream is independently
  counted and rejected once decoded bytes exceed 5 MiB. Request/response/stream
  ownership is settled on failure or cancellation. No unbounded ReadAsStringAsync.

## MIME and Result Contract

The current TS source is TEXT-ONLY. It rejects binary images and PDFs; the prior
request's suggested image/PDF attachments are not part of the actual source leaf.
Image MIME types other than SVG and `image/vnd.fastbidsheet` receive the source
image-type error. The latter is still rejected by the subsequent non-text MIME rule.
PDF and other non-text MIME types receive the file-type error.

Accepted types are missing Content-Type, `text/*`, JSON and `+json`, XML and `+xml`,
and JavaScript MIME variants. SVG is therefore accepted as textual XML. There is no
content sniffing. Bytes are UTF-8 replacement-decoded and the initial BOM removed;
advertised charset is not used, matching the source TextDecoder behavior.

As in the source, HTML conversion is selected by the case-sensitive `text/html`
substring in the original Content-Type header. XHTML/SVG/JSON/plain text otherwise
pass through unchanged, even for markdown format. HTML format returns original
decoded HTML, not sanitized or reserialized markup.

Structured output is exactly `{ url, contentType, format, output }`, retaining the
original URL. Content is one canonical text part with that output. Metadata is
`{ contentType }`. No binary file part or pretend managed-output file is returned;
large-result storage belongs to the Session output pipeline.

## HTML Conversion

AngleSharp supplies a real inert HTML5 parser. The renderer ports the source state
machine rather than stripping markup with regular expressions:

- Omitted script/style/noscript/iframe/object/embed/meta/link/template content;
  hidden/aria-hidden/head suppression and closed-details summary handling.
- Headings, blocks, line breaks, rules, emphasis, strike-through and definition lists.
- Escaped text, link destinations/titles and image Markdown syntax without fetching
  those resources or resolving relative URLs against a different base.
- Ordered/unordered/nested lists, start/value ordinals and bounded indentation.
- Blockquotes, inline code and fenced code with source whitespace/fence handling.
- Table cells/captions, rectangular pipe tables, span/ragged-table fallback, nested
  table text flattening and quote/indent prefixes.
- Raw code chunks bypass ordinary whitespace normalization. Output uses the source
  5 MiB Markdown budget and 64 KiB closure reserve with Unicode-safe byte slicing.

Plain-text extraction uses its distinct, smaller source omission set and concatenates
decoded text without invented block separators. Template content is traversed for
plain text but omitted for Markdown. HTML5 parsing is not execution or sanitization;
raw html format and preserved link destinations remain untrusted tool content.

## Exact Limitations

AngleSharp's HTML5 tree repair, implied elements, foreign content handling and
CR/LF/NUL normalization can differ from htmlparser2 for malformed markup/fragments.
This is not a claim of byte-for-byte parser parity. Traversal is iterative and the
source depth-10000 Markdown fallback is retained, but DOM allocation occurs before
that rendering fallback. Input is bounded to decoded responses of at most 5 MiB.

A code wrapper that cannot fit the reserved budget fails explicitly instead of
repeating the source helper's potential non-progress trimming loop. Text-only regexes
for whitespace/escaping have non-backtracking execution and a timeout; none parse HTML.
URI parsing follows .NET's absolute URI rules rather than every permissive WHATWG URL
spelling. Native decompression/header normalization may differ from fetch while the
actual decoded-body limit remains enforced. No parser/conversion runtime fixtures
were executed under the build-only verification constraint.

## Dependency Verification

AngleSharp is pinned to stable 1.7.2, which includes the fix for
`GHSA-pgww-w46g-26qg` / `CVE-2026-54570` (affected versions below 1.5.0). NuGet metadata
lists a native net10.0 asset and no vulnerabilities for this version. Restore/build
verification uses NuGet auditing; no advisory suppression was added. The earlier
1.3.0 dependency was replaced, not retained with a warning exclusion.

Only dependency/advisory metadata requests and dependency restore/build were allowed
for verification. No webfetch invocation, application HTTP request, test, provider,
database operation or process-control command was executed.
