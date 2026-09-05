# Persistent PTY backend

This is a managed client for the **protocol-7 native `opencode-pty` daemon**, matching
`packages/core/src/persistent-pty/index.ts` and `daemon.ts`. It is not an ordinary
ConPTY session relabeled as persistent. The native daemon owns PTYs, controller
generations, spool retention, foreground-process detection, emulator snapshots,
and checkpoints. The .NET service uses the actual returned facts and bytes.

## Supported deployment

Explicit build packaging can supply a pinned, hash-verified native executable.
Overrides `OPENCODE_DOTNET_PTY_BIN`, source `OPENCODE_PTY_BIN`, and a native PATH
candidate remain supported. There is no runtime download, guessed native
library export, npm/Node launcher, or redirected-process terminal fallback. Missing
executables and protocol/identity mismatches are errors. Automatic daemon launch
uses the pinned .NET process API on Windows/Linux; transport supports Windows
named pipes and Unix-domain sockets. A compatible platform daemon is required.
Neither platform transport nor native launch was exercised during implementation.
Upstream 0.1.13 publishes no Windows daemon artifact. See `build/PTY-ASSETS.md`
for exact origin/hash/license evidence, explicit packaging, and capability limits.

Default runtime directories are unique under `opencode-pty-dotnet` in the OS temp
directory, or a unique child of `OPENCODE_DOTNET_PTY_RUNTIME_DIR`. They do not
discover another OpenCode channel's daemon. Explicit handoff is the only normal
way to adopt an existing instance.

## Ownership and mutation ordering

- Discovery checks registration protocol, then authenticated pong instance/PID.
- One startup gate serializes discovery/spawn/ownership. The `own` connection
  stays open for the host lifetime; a reader observes closure and invalidates the
  cached registration. Handoff exchanges use that same owner connection.
- Requests are 4-byte big-endian lengths plus JSON, bounded to 8 MiB. A dispatched
  mutation whose response is lost is not retried. Authentication rejection and
  pre-dispatch connection failure have the source's limited retries.
- Only create starts a daemon. List on an authoritative absent/refused daemon is
  empty; corrupt registration, wrong protocol, and unexpected replies remain errors.
- Startup failure terminates/reaps only the exact contender handle this attempt
  created, never a PID read from registration.
- Create/remove publish canonical persistent-PTY events after daemon success.
  Controller attachment, control/input, and resize select the current terminal;
  reads and observer attachment do not. Selection is process-local and resets on
  restart. Read returns null when there is no selected terminal.
- Snapshot/read use actual daemon emulator output. No transcript stripping,
  fabricated cursor, inferred foreground process, or fake checkpoint is used.

## Handoff

`ServerHost.CreateApp(..., persistentPty: options)` accepts `PersistentPtyOptions`
with the existing Schema handoff. Startup verifies expiry and instance identity,
then claims it before ready health. The handoff ticket is issued by the native
owner protocol, not synthesized by the Server.

Automatic idle build replacement calls the old authenticated Server's handoff
endpoint before cooperative stop. The one-use value goes only into the new
daemon's child environment. ServerHost consumes and clears it before tools or
terminal children can inherit it; it is not written into argv or service config.
Explicit stop remains a stop. Crashes, expired handoffs, and failed adoption do
not carry an exactly-once or indefinite-survival guarantee.

## HTTP/WebSocket

All eleven operations from `packages/protocol/src/groups/persistent-pty.ts` are
mounted. Terminal operations use the native daemon's global IDs/session group;
they do not manufacture Session rows or Location envelopes. Ordinary PTYs remain
Location-owned and separate.

Connect tickets share the existing one-use ticket service but use only persistent
PTY identity as scope. Ticket issue requires the preflight-forcing header and
allowed origin. Connect consumes tickets before upgrade. The narrow middleware
ticket exception grants no authorization by itself.

One outbound writer orders attached metadata, binary replay, replay_complete,
then live bytes and control notifications. Framed input uses the source type byte
and big-endian uint16 dimensions. The daemon decides controller ownership; rejected
input does not become a fabricated success response. Attachments cancel/dispose
their own transports without deleting the terminal. Visible exit schedules the
source's terminal removal operation.

`SessionHttpClient.PersistentPty.cs` provides typed HTTP operations and a caller-owned
raw `ClientWebSocket` connection. Text messages are protocol control JSON; binary
messages are terminal output bytes. The caller owns socket disposal, framing,
cursor tracking, and deliberate reconnection. No reconnect/replay success is guessed.

Verification is pinned .NET compilation only. No daemon, shell, PTY, native call,
socket connection, handoff, database operation, test, or provider was run.
