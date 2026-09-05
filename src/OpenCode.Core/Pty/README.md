# Local PTY Runtime

Build-verified Windows ConPTY implementation. Native behavior has not been run or
tested. Unix throws `PlatformNotSupportedException`; redirected standard streams
are not substituted for a PTY.

## Source Coverage

Read the complete upstream `packages/core/src/pty.ts`, `pty/pty.*`,
`pty/protocol.ts`, `pty/ticket.ts`, `shell/select.ts`, all six files under
`persistent-pty`, and both server handlers before implementing this slice.

- `PtyService`: basic `list`, `get`, `create`, `update` (title and resize),
  `remove`, `write`, `attach`, Location teardown, retained exit metadata.
- Output replay retains 2 Mi UTF-16 code units, with absolute UTF-16 cursors,
  tail cursor `-1`, deferred activation, detach, and output-before-end ordering.
- Live output includes both original bytes and incrementally decoded UTF-8 text.
  Raw byte input is available independently of a future WebSocket text validator.
- At most 25 exited terminals remain registered. Removing one releases its buffer.
- Shell resolution is a required injected callback; login-shell arguments follow
  the source shell metadata. Parent environment, input overrides, TERM,
  OPENCODE_TERMINAL, and Windows UTF-8 locale overrides follow basic PTY creation.
- `PtyTickets`: process-global 60-second, one-use tickets, capacity 10,000,
  matching PTY ID plus exact directory and optional workspace identity.

## Native Boundary

Windows 10 build 17763 or later is required. Bindings use `LibraryImport`,
`SafeFileHandle`, `SafeProcessHandle`, and distinct owned HPCON/thread handles.
HPCON is released with `ClosePseudoConsole`, never `CloseHandle`.
Synchronous input/output pipes are serviced separately; output continues draining
while the pseudoconsole closes. No cursor-inheritance query flag is enabled.

The installed .NET 11 preview Process API has no STARTUPINFOEX attribute support.
This specific creation path therefore uses `CreateProcessW` with the documented
`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` attribute, not `Process.Start` with ordinary
redirected pipes. Persistent PTYs use a separate native daemon transport; see
[PERSISTENT.md](PERSISTENT.md).

API and layout references:

- https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session
- https://github.com/microsoft/terminal/blob/main/samples/ConPTY/EchoCon/EchoCon/EchoCon.cpp

The upstream `@opencode-ai/pty` binding exposes a daemon executable path, not a
verified C ABI. No unverified library exports have been invented or invoked.

## Parent Integration

Create one `PtyService` per resolved local Location lifetime. Dispose it when that
Location closes. Do not register it as a per-request service or as Session state.
Explicit workspace placement must be resolved by the Location owner before using
this local runtime; it must not silently become implicit-local placement.

`PtyLocationMap` owns one runtime per resolved Location and drains its event bridge
on invalidation or host teardown. `Server/Pty` now includes the actual WebSocket
adapter, origin policy, event bridge, and DI registration extension. The existing
PTY endpoints use them instead of the fake empty list. ServerHost now supplies
the supported native Location flush: actual config/command loading, rejection of
unsupported plugins, shared MCP observation, and registry reload. This is not a
blanket exception or an empty completed task. See `../../OpenCode.Server/Pty/HOST-INTEGRATION.md`.

Authoritative request query/header Location resolution, shell selection,
CORS/WebSocket middleware, one-use ticket validation, and Location invalidation
are wired. Windows PTY and WebSocket behavior still require authorized runtime verification.

No Session tool permission bypass is provided. These are authenticated basic
terminal services, not an agent shell tool. Persistent Session ownership,
controller/observer roles, byte replay, snapshots/checkpoints, and handoff now use
the separate protocol-7 daemon described in PERSISTENT.md. Ordinary PTYs do not
acquire those guarantees and still end with their owning Location.

## Verification

Only this build was used for final verification, with the repository SDK:

```powershell
.\.dotnet\dotnet.exe build src/OpenCode.Server/OpenCode.Server.csproj --artifacts-path C:/tmp/opencode/pty-ws-build -v:minimal
```

Result: zero warnings, zero errors, including Core, Schema, and Protocol.
No tests, application, terminal, child process, database, provider, or native PTY
calls were executed for verification. No commits were created.
