# Network headless run

Command parsing/help now belong to the single typed System.CommandLine tree in
CommandLine/CliApplication.cs. RunCommand receives RunOptions directly; the old
RunOptions.Parse and argv/help scanner were removed. See CommandLine/README.md.

Source read: `packages/cli/src/commands/commands.ts`, handlers/run.ts, run/run.ts,
run/noninteractive.ts, run/ui.ts, session-target.ts, env.ts, util/io.ts,
`packages/util/src/fs-util.ts`, and the TUI tool-output text helpers.

## Replaced behavior

Program.cs now dispatches run into this subtree. It no longer joins option names
into the prompt, inserts "Hello from opencode-dotnet!", invokes the embedded SDK's
Ask loop, or imposes a two-minute execution deadline. The command uses the existing
dotnet ServiceDaemon and authenticated SessionHttpClient. No server execution,
tool loop, SQL, or model invocation is reimplemented in the CLI.

## Arguments and input

Supported source flags: --continue/-c, --session/-s, --fork, --model/-m,
--agent, --format default|json, --file/-f (up to 100), --title, --thinking,
--auto and the hidden source aliases --yolo/--dangerously-skip-permissions,
--server and --standalone. --help/-h before the literal -- separator prints help
without connecting or reading stdin. Unknown options fail, not become prompt text.
Boolean literals, source short clusters and --no-name negation are handled by the
shared tree compatibility layer. Repeated scalar flags use the source first value;
repeat --file/-f for multiple files. Unknown option-shaped arguments before -- fail.

Source positional formatting is preserved: arguments containing spaces are
quoted, with internal quotes escaped. Redirected stdin is appended with one
newline; no stdin read occurs for a terminal. Empty/whitespace combined input is
an error even when files are supplied. Local cwd is selected using PWD then cwd,
matching source; that operation only occurs on an actual user invocation.

Files are read before Session creation, capped at the initial 10 MiB length and
100 arguments, with UTF-8 roundtrip and source control-byte/binary checks. Plain
text becomes the source `<file name="...">` prompt block. Supported image/PDF
files become actual inline data-URI attachments, not server-local file paths.
MIME lookup now uses all 1,239 extension mappings generated from installed
mime-types 3.0.2 / mime-db 1.54.0, including the source conflict-score/tie rules.
Unknown extensions use the source application/octet-stream fallback. Non-image,
non-PDF valid nonbinary UTF-8 still becomes text/plain regardless of its registry
type, as source does. See MIME-REGISTRY.md for pinned inputs and regeneration.
Special-file detection uses native managed attributes/seekability;
platform behavior has not been runtime-verified.

## Session selection

Explicit session IDs use GetAsync and preserve not-found/API errors. --continue
pages top-level Sessions descending, 50 per page, matching the current directory
and implicit-local workspace; no match creates a new Session. --fork requires
continue/session and calls the real through-boundary fork endpoint. New Sessions
use the requested agent/model/location. Existing Sessions retain selections unless
overridden; switching occurs through real agent/model API operations.

--title only applies to a newly created Session. An explicitly empty title uses
the source first-50-message-characters rule. Managed local runs send the current
Session environment through SetEnvironmentAsync, excluding source password names
and the native explicit-server password. Explicit remote runs do not upload that
environment. No environment or credential values were read during verification.

The client currently lacks source location.get; selection uses locally normalized
cwd and the existing Session APIs. Server-normalized remote directory aliases may
therefore differ from the complete source session-target resolver. A typed Location
getter is a remaining client handoff, not grounds to guess a project-root directory.

## Admission, stream and completion

The command subscribes before admission, allocates one canonical MessageId,
submits one steer, and observes actual inbox-delivered and execution events for
the selected Session. No retry creates another prompt after a lost response.
Pre-promotion events do not become this prompt's output. Event-stream failure
does not imply successful completion. The finalizer closes only the client stream.

The authoritative WaitAsync call has no artificial execution timeout. After wait,
message pages (200, descending) are scanned to the submitted ID, then reconciled
in ascending order. Text/reasoning prefixes and completed tool IDs suppress already
rendered content. No archive/event history is synthesized or persisted. The
source projected part-ID formatting is only presentation metadata. A missing
promoted prompt fails unless a source permission/form/cancellation path explains it.

The command emits source-shaped JSON lines: step_start, text, reasoning (only
with --thinking), tool_use, step_finish, and error, with timestamp/sessionID and
actual event/projected fields. Text is printed on stdout when redirected; terminal
text/step labels and tool notices use stderr as source UI does. Tool output text
uses the source shell-first-text rule and read-envelope unwrapping. Default tool
output now uses the production run callbacks' icons/titles/descriptions/block-body
rules for shell, read/write/edit/patch, glob/grep/list, LSP, webfetch/websearch,
subagent, question, skill, batch and invalid tools, including bash/task/apply_patch
aliases. Unknown tools use the source gear/name/input fallback, not a raw output
dump. Run UI blank-line/reset/style behavior is shared across text and tool output.
This ports the run callbacks only, not TUI scroll/snapshot renderers. Absolute
Windows/POSIX display paths are handled lexically; drive-relative Windows paths
requiring per-drive process context fall back to source-style unknown rendering.
JSON remains separate from permission warnings and other stderr notices.

## Headless permissions and forms

This source run mode does not present interactive forms, even with terminal
stdout. Requests for this Session are rejected unless an explicit --auto/alias
was supplied. Auto replies once, never always; explicit server-side denials are
not overridden. Rejection prints the source warning and requests interruption.
Session-owned forms are cancelled, never answered using defaults or --auto.
Attached clients ignore global MCP forms belonging to potentially unrelated work.

Pending request snapshots after admission close the subscription gap; errors in
these advisory snapshots are tolerated as source does, without replacing actual
wait/message reads with fake empty state. Reply races are tolerated; already-settled
form errors are distinguished from other failures. Concurrent snapshots/events are
deduplicated by request ID. Output/reconciliation is serialized separately.

Source rejection/cancel paths do not universally set exit code 1: if they end in
a user interruption without an emitted execution error, source returns normally.
This behavior is retained rather than inventing a successful model answer.
Actual execution/API/stream errors return 1; caller cancellation returns 130 and
requests interruption of the selected Session. Failure to confirm interruption
is reported, not a claim the server has stopped. The native handler now implements
source's second-SIGINT immediate process.exit(130) behavior. It exits only this
CLI process, without enumerating or killing a server process. As in source, this
forced path bypasses graceful finalizers; it does not claim owned jobs settled.

## Connection and remaining boundaries

Managed discovery/start is only through the dotnet ServiceDaemon. Explicit-server
inspection never starts/replaces the elected service and uses
OPENCODE_DOTNET_SERVER_PASSWORD. Native lifecycle readiness/build policy remains
in effect rather than spawning an official launcher or custom process loop.
--server and --standalone conflict before connection.

Standalone now uses the shared StandaloneHostLease with the Server-owned private
factory. It uses actual authenticated HTTP, not a data shortcut. The lease remains
alive for Run's cancellation/Session-interrupt handling, then the client closes
before owned host shutdown. No managed fallback, recovery sweep, or managed Session
environment upload occurs in standalone mode. See Hosting/StandaloneHost/README.md.
No TUI/root markup was edited. Program's TUI branch now also supports the same
lease for tui --standalone and root --standalone; run dispatch remains separate.

## Evidence

The final ordinary isolated pinned .NET 11 full-CLI build passed with zero warnings/errors
using OpenApiGenerateDocuments=false. The MIME source was imported by an explicit
source-only MSBuild step on a prior full CLI build; no upstream JS was executed.
Verification was compilation only: no
Program/run/stdin execution, file preparation, service start, API/SSE call, SQL/DB
access, native/model execution, tests, Git commands, filesystem/network probes or
delegation occurred. Build success does not prove stream/race or output parity.

## Source license

Adapted from OpenCode, MIT License, copyright (c) 2025 opencode. Permission is hereby
granted, free of charge, to any person obtaining a copy of this software and its
documentation (the Software), to deal in the Software without restriction,
including rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies, and permit others to do so, subject to inclusion of this
copyright and permission notice in copies or substantial portions. THE SOFTWARE
IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR ITS USE.
