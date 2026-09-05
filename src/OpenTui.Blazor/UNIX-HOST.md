# Unix reader integration

`OpenTuiHost` now selects its reader from `NativeTerminal.InputKind`:

- `WindowsConsoleEvents`: the existing `WindowsConsoleInput` record reader.
- `UnixBytes`: the Unix owner's `UnixTerminalInput.ReadFrame()` implementation,
  which owns incremental UTF-8/protocol delivery and its per-frame read budget.

Initial Unix dimensions and resize polling use `NativeTerminal.GetUnixSize()`.
The Unix branch never calls Console.ReadKey, Console.KeyAvailable, Console.In,
Console.TreatControlCAsInput, or Console dimension APIs. A budget-exhausted Unix
frame is not forcibly flushed as Escape; the reader controls parser flushing.
EOF drains already decoded events, then ends the host input lifetime.

The reader is disposed before `NativeTerminal`, after app/event callbacks stop.
The existing native owner retains all termios capture/restoration and supported
ABI checks. There is no guessed dimension fallback, alternate termios owner,
signal handler, or raw-ANSI renderer in this integration.

This does not package Unix native assets or establish runtime portability.
Musl/unsupported architectures remain gated by NativeTerminal. Clipboard remains
host-injectable; the default Windows clipboard adapter is not a Unix clipboard
implementation. See `OpenTui.Native/NativeTerminal.Unix.md` for the native owner's
precise ABI and restoration limits.

Only source inspection and pinned .NET 11 full-graph build attempts were used.
No terminal, clipboard, Unix/native program, or runtime probe was executed.
