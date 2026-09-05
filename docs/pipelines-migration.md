# Application transport Pipelines migration

## Inventory recorded before implementation

Scope: application-owned byte acquisition, buffering and record/message framing.
No provider retries, model/tool execution loops, ID types, command parsing, or
third-party HTTP/MCP internals are part of this change.

| Files | Owned boundary / preservation requirements |
| --- | --- |
| `src/OpenCode.Core/Llm/LlmClient.cs` | Provider SSE; strict initial UTF-8 with StreamReader BOM detection; CR/LF/CRLF; partial data/error event at EOF is invalid provider output; event filtering and error dispatch unchanged. |
| `src/OpenCode.Client/SessionHttpClient.Events.cs` | Client SSE; strict UTF-8, only initial UTF-8 BOM stripped; 16 Mi UTF-16-character event budget (CRLF counts once); accepts final unterminated data block. |
| `src/OpenCode.Core/Pty/PersistentPtyDaemon.cs`, `PersistentPtyService.cs` | Four-byte BE length prefix, 8 MiB payload limit, empty frames, EOF/truncated headers/payloads, connection-owned reader across handshake and subsequent frames; dispatched/retry classification unchanged. |
| `src/OpenCode.Client/PtyConnection.cs` | WebSocket fragments, close during message, strict UTF-8, binary cursor record; no new message limit. |
| `src/OpenCode.Server/Pty/PtyWebSocket.cs`, `PersistentPtyWebSocket.cs` | WebSocket fragments; ordinary invalid UTF-8 discarded and binary BOM stripped; persistent five-byte input header and rejection policy retained. |
| `src/OpenCode.Cli/Tui/Terminals/PersistentTerminalController.cs` | WebSocket messages and output batching; copied bytes must outlive read advances; resize/replay barriers, render yield and send ordering unchanged. |
| `src/OpenCode.Cli/Commands/Api/ApiCommand.cs` (response method only) | Raw HTTP incremental UTF-8 replacement decoding, initial BOM strip, no flush between surrogate halves, final platform newline. No command/argument parsing edits. |
| `src/OpenCode.Core/Tools/HttpBody.cs` | Bounded HTTP body collection, declared/observed byte limits and domain error unchanged. |
| `src/OpenCode.Core/Session/SessionPromptPreparation.cs`, `Tools/LocalFileMutation.cs`, `Tools/Builtins/ReadTool.cs` | Bounded input/media collection; preserve initial sniff bytes, seek boundary, byte limits, and existing text paging/truncation semantics. |
| `src/OpenCode.Core/Tools/OwnedToolProcess.cs` | Redirected output ingestion and LF search records (optional preceding CR); 1 Mi UTF-16 record limit; early cutoff/kill ownership unchanged. |
| `src/OpenCode.Core/Integrations/IntegrationCommands.cs`, `Formatting/LocalFormatter.cs` | Redirected incremental text reads; stderr/truncation semantics remain application text operations. |
| `src/OpenCode.Core/Pty/WindowsPty.cs`, `PtyService.cs` | Native ConPTY stream ingestion and incremental decoded output; native handle/read interruption contract and semantic output channel retained. |
| `src/OpenTui.Blazor/TerminalInput.cs` | Incremental Unix UTF-8 pending bytes, raw X10 coordinates and EOF rejection. VT grammar/key state machine remains intact. |
| `src/OpenTui.Blazor/Code/TreeSitterGrammarCache.cs` | Bounded asset/cache stream collection only; pinned assets, registry, hashes and parser remain unchanged. |
| `src/OpenTui.Native/NativeEmbeddedTerminal.cs` | Batch collection of separately borrowed native response chunks; native API/lifetimes unchanged. |
| `src/OpenCode.Server/Endpoints/EventEndpoints.cs` (added by removal audit before editing) | Server SSE output uses framework-owned BodyWriter; retain start/flush/heartbeat order and serialized writes. |

## Explicit exclusions

- Schema, ID definitions, CLI command parsing/Program, and other CLI transport
  consumers outside the response method/terminal-controller ownership above.
- `HttpClient`, ASP.NET request/response bodies and WebSocket protocol internals,
  MCP SDK streams, SQLite readers, JSON serializers, compression codecs and
  framework `StreamReader` encoding/BOM detection: use their public APIs, not
  replacement protocol/decoder implementations.
- Semantic Channels, PTY output history, prompt/tool fragments, text diff/Markdown
  builders, UTF-16 editor state, bounded display-line previews, and queues of
  already-owned complete messages are not wire-byte accumulators.
- Native WASM linear memory, clipboard ABI packing, text/graphics FFI, console
  records, poll/read handles, and bounded VT grammar scratch state are not stream
  framing. Native resource loading via framework CopyTo and JSON serialization
  into a MemoryStream are complete-object serialization, not incremental framing.
- CLI image loading was initially outside the transport worker's ownership. The
  final integration pass also migrates its stream collector; image codecs, access
  authorization, redirect policy and complete data-URI decoding remain unchanged.
- ReadTool's 2002-character display preview/binary detection is a semantic paging
  algorithm, not a wire record accumulator; its underlying byte ingestion is in
  scope. No grammar/query asset content changes.
- The OpenTui bracketed-paste payload accumulator is included in addition to its
  UTF-8 byte ingress; only the fixed 4096-character VT grammar scratch array and
  key/control-state transitions are excluded, not unbounded paste assembly.

## Implementation requirements

Use a neutral shared transport project, with System.IO.Pipelines as a package
dependency when not framework-provided; do not introduce ASP.NET into Core or
OpenCode dependencies into generic OpenTui. Stream adapters leave streams open
unless they own the complete connection. Each borrowed ReadOnlySequence is
consumed before AdvanceTo; any dispatched/queued result owns its bytes/text.
Every reader/writer is completed on EOF, error, cancellation and early consumer
exit. No background read-ahead across semantic dispatch/retry boundaries.

For complete-message/record assembly with no concurrent consumer, a Pipe's
automatic writer pause must be disabled: otherwise a legal frame larger than
the pause threshold deadlocks before it can be emitted. Existing protocol limits
remain authoritative; this is not permission to introduce unbounded work queues.

## Implemented transport surfaces

The neutral `src/Transport.Pipelines` project supplies the shared infrastructure.
It references exact `System.IO.Pipelines [10.0.0]`, compatible with the pinned
.NET 11 SDK. It has no ASP.NET, OpenCode, Schema, SDK, or native dependencies.
Core and Client reference it directly; OpenTui.Native references it for its
managed response-batch collector. No ASP.NET framework reference was added to Core.

- `FrameConnection`: connection-lifetime PipeReader/PipeWriter and SequenceReader
  BE framing, preserving buffered bytes across the PTY subscription handshake.
  EOF on empty or partial frames remains EndOfStreamException. Oversize headers
  remain daemon `protocol` errors. Successful frames own their byte arrays.
- `PipelineWebSocket`: receives directly into Pipe memory and emits an owned
  complete-message byte array. A close discards a partial message. No new size
  limits, background pumps, message coalescing, socket ownership, or semantic
  queue changes were introduced.
- `PipelineBytes`: bounded stream collection over PipeReader, retaining bytes
  until a complete owned result can be returned. Optional sniffed prefix bytes
  count toward the same original budget. Caller stream ownership is explicit.
- `PipelineText`: PipeReader stream ingestion with framework StreamReader encoding
  semantics. The provider uses framework line reads to retain the exact line-level
  decoder/error dispatch boundary; reading larger decoded chunks there could
  discover malformed input before an earlier completed provider event dispatches.
  Client SSE instead retains its original chunk-level decoding boundary and uses
  SequenceReader over UTF-16 Pipe records for its CR/LF/CRLF and dynamic remaining
  event-character budget. No manual replacement UTF-8 decoder was introduced.
- `SequenceBuffer`, `TextRecordBuffer`, `TextSequenceBuffer`: segmented assembly
  for complete socket messages, decoded search records, native response batches,
  terminal output batches, Unix pending UTF-8, and bracketed-paste payloads.
- Server SSE writes through ASP.NET's existing BodyWriter, retaining response
  start, per-frame flush and serialized heartbeat/live dispatch. The framework
  owns completion of that writer.

SSE event field values are owned strings and assembled with string.Join at the
semantic event boundary. The provider retains its unfinished-event error body
(including its trailing newline); the client retains its accepted final data
block. Empty data fields, comments, named error events and filtering are unchanged.
Raw API output retains replacement UTF-8, first BOM removal, surrogate-safe
TextWriter flushes and platform-newline suffix handling. Its command parsing and
Program were not edited.

Native ConPTY ingestion uses a PipeReader on the existing dedicated reader task.
The original broken-pipe error classification and semantic output channel remain.
PtyService's framework Decoder remains intentionally unchanged: it pairs each
already-owned raw byte chunk with decoded text and flushes replacement characters
at EOF. Reframing that semantic channel would change its byte/text pairing.

## Disposal and borrowed memory review

All queued/dispatched results own bytes or strings before Pipe readers advance.
Borrowed sequences are confined to a single parse/copy operation. The native
response drain receives a span only for the duration of that synchronous FFI call.
Terminal input storage is disposed after the Unix input reader, not before it.

FrameConnection cancellation closes its owned stream, waits for the active
reader/writer, and completes both pipelines. StreamPipeWriter normally flushes
on CompleteAsync (verified in dotnet/runtime v10.0.0 source); failed writes and
disposal explicitly complete with an error to discard any remaining frame bytes.
They cannot be resent by cleanup or a later write. The existing daemon dispatch
flag and retry guards are untouched. Stream adapters otherwise leave caller-owned
streams open. No timing/backpressure/performance measurements were performed.

## Removal audit

Removed the hand-built daemon header/payload reads and combined write buffer;
all four socket fragment MemoryStreams; client SSE character loop and old
ReadEventChunkAsync; provider SSE StringBuilder assembly; search-record
StringBuilder; raw API custom read loop; owned bounded body/file/asset
MemoryStreams; native response and terminal batching MemoryStreams; pending
Unix four-byte shift buffer; and bracketed-paste StringBuilder.

Remaining search hits were inspected and classified:

| Remaining construct | Reason retained |
| --- | --- |
| `Core/WebSearch/NativeWebSearchProvider.cs` MemoryStream and `Core/CodeMode/JintCodeModeRealm.cs` ArrayBufferWriter | Complete-object Utf8JsonWriter serialization, not streaming/framing. |
| `Protocol/Documentation/CanonicalOpenApi.cs`, `OpenTui.Blazor/Code/TreeSitterAssets.cs` MemoryStream | Framework copying/decompression of embedded resources; not an application transport accumulator. |
| `Core/Session/SessionPromptPreparation.cs` List<byte> | Percent-decoding an already complete data URI, not stream framing. |
| `Core/Tools/Builtins/ReadTool.cs` 2002-character preview | Semantic display truncation/binary detection and paging; byte ingress migrated. |
| `Core/Formatting/LocalFormatter.cs` StringBuilder/StreamReader | Semantic bounded help text and framework file inspection; process ingress goes through migrated OwnedToolProcess. |
| `Core/Tools/ShellProcessSource.cs`, `Core/Shell/ShellRuntime.cs` ReadAtLeastAsync | One bounded random-access spool-file snapshot, deliberately not a tailing stream; no custom accumulator. |
| `OpenTui.Blazor/UnixTerminalInput.cs` 4096-byte array | Fixed native read scratch space within the original dispatch byte budget; pending bytes are now in the parser's Pipe. |
| `OpenTui.Blazor/TerminalInput.cs` 4096-character scratch | Bounded grammar/parser state, not a byte transport queue; paste and pending byte storage migrated. |
| `Cli/Tui/Images/ImageSourceLoader.cs` data-URI MemoryStream | Synchronous encoding of an already complete string, not stream framing. Its asynchronous stream collector now uses SequenceBuffer. |
| `Cli/Tui/InteractiveTui.cs` MemoryStream | Read-only adapter over a complete Client-owned response byte array; no custom streaming accumulator. |
| `Cli/Commands/Run/RunFiles.cs` fixed byte array | One file snapshot bounded by its captured initial length, not a growing stream collector. Preserve truncation at that length even if the file grows. |
| Server EventFeedService frame strings and outbox Channels | Complete semantic event serialization and ordering, not bespoke byte buffering. |

No eligible old accumulator remains in the owned inventory. No Schema/ID
definition edits or ID API changes were needed; existing Create/FromExisting calls
remain. The final full CLI dependency build passed after cleanup review with
0 warnings and 0 errors (53.87 seconds). Runtime behavior remains unverified under
the build-only restriction.

Verification is build/source inspection only: repository `.dotnet` .NET 11
preview 7, isolated `C:\tmp\opencode\pipelines-migration` artifacts,
`OpenApiGenerateDocuments=false`. No tests, samples, sockets, parser/provider,
native/WASM, API/DB, network or application execution. No zero-copy claim.

## Final CLI integration

The final integration review expands the original worker boundary to include
`Tui/Images/ImageSourceLoader.ReadBounded`. It now acquires stream bytes directly
into the existing neutral `SequenceBuffer` and returns an owned byte array at EOF.
The CLI references `Transport.Pipelines` directly, also making its existing API
and terminal transport usages explicit rather than relying on transitive references.

- Reads retain the 64 KiB maximum request and the one-byte overflow probe.
- The encoded-image limit and `NativeImageException(MemoryLimit)` are unchanged.
- Cancellation remains at the same stream-read boundary. Continuations retain
  their existing caller context; the collector does not take stream ownership.
- The pipe is disposed on EOF, cancellation and error. Overflow bytes are not
  committed, decoded, or passed to native code.
- Complete data-URI conversion and fixed-length file snapshots remain deliberate
  non-streaming exceptions, not unfinished Pipelines adapters.

The earlier zero-warning build above predates this integration and EF work. The
September 5 full CLI integration build passed with 0 errors and 1,979 existing
analyzer warnings, none in CLI. See the modernization completion record for the
current cleanup status. No image/codec/native execution is part of this review.
