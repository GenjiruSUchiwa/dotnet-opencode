# Terminal clients

Ordinary Location PTYs: `ListPtysAsync`, `CreatePtyAsync`, `GetPtyAsync`,
`UpdatePtyAsync`, `RemovePtyAsync`, `IssuePtyConnectTokenAsync`, and `ConnectPtyAsync`.
Inputs are existing Schema `PtyCreateInput`/`PtyUpdateInput`; responses retain the
canonical Location envelope. Ticket minting sends `x-opencode-ticket: 1`.

`ConnectPtyAsync` returns a disposable `PtyConnection`. Read events distinguish
terminal text, the post-replay absolute UTF-16 cursor, and close code/reason.
`WriteAsync` sends text input with serialized writes. No automatic reconnect or
terminal creation occurs. Dispose the connection when a reader is abandoned;
detaching does not remove the server terminal. Use Update for resize.

Persistent PTYs use the separate `*PersistentPty*` methods and protocol. Their
HTTP envelopes do not contain Location. Null terminal-read and null handoff values
are preserved as null, not replaced with empty/fake state. `ConnectPersistentPtyAsync`
returns a caller-owned `ClientWebSocket`: binary frames are output, text frames
are control JSON. With framed input enabled, send type 0/1 plus big-endian uint16
columns/rows followed by input bytes for type 1. Controller ownership is reported
by the Server, not inferred from a successful WebSocket send.

Source: `packages/protocol/src/groups/pty.ts`, `persistent-pty.ts`,
`packages/core/src/pty/protocol.ts`, and both Server PTY handlers. No Core/Server
runtime dependency was added to Client. Native and network behavior is unverified;
only isolated builds were run.
