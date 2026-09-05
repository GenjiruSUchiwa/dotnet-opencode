# Unix NativeTerminal mode ownership

This supersedes the blanket Unix-wrapper blocker in the existing Native README.
It does **not** establish end-to-end Unix Blazor/TUI support.

## Supported wrapper ABIs

| Platform | Process architecture | C termios layout | Library |
| --- | --- | --- | --- |
| Linux glibc | little-endian x64, arm64; 64-bit pointers | 60 bytes: four u32 flags, u8 line discipline, 32 control bytes, u32 input/output speeds at offsets 52/56 | .NET `libc` name → `libc.so.6` |
| Linux musl | little-endian x64, arm64; 64-bit pointers | Same 60-byte public layout; trailing speed fields are named `__c_ispeed`/`__c_ospeed` in musl | .NET `libc` name → `libc.so` |
| macOS | little-endian x64, arm64; 64-bit pointers | 72 bytes: four u64 flags, 20 control bytes, u64 input/output speeds at offsets 56/64 | `/usr/lib/libSystem.B.dylib` |

Android, other Unix platforms, 32-bit processes, other architectures, and
big-endian ABIs are explicitly unsupported. Missing libc/native renderer symbols
fail rather than select a guessed ABI. Matching OpenTUI native assets still need
deployment; this change does not package a Linux/macOS renderer or download one.

### Linux libc selection and ABI proof

`NativeTerminal.Linux.cs` is the named Linux boundary. It recognizes the exact
portable runtime IDs `linux-x64`, `linux-arm64`, `linux-musl-x64`, and
`linux-musl-arm64`, with matching process architecture. Unknown/distro-specific
runtime IDs are rejected instead of guessing their libc ABI. No native version
probe, NativeLibrary.Load call, or additional process-wide DLL resolver is added.

Common LibraryImport declarations use `libc`, the name used by .NET 11's own
Interop.Libraries. The pinned CoreCLR `FixLibCName` implementation maps it to
glibc's `LIBC_SO` when available, otherwise `libc.so` on musl. Musl's Makefile
builds `lib/libc.so`; the architecture-specific `ld-musl-*.so.1` installation is a
link to that same shared libc. A musl process is not directed to glibc symbols.
This resolution follows the runtime loader; it was not probed during this work.

The inspected musl v1.2.5 `include/termios.h` defines cc_t as unsigned char,
tcflag_t/speed_t as unsigned int, and NCCS=32. The generic bits/termios.h layout
is four flags, c_line, c_cc[32], then two speed fields. Its x86_64 and aarch64
alltypes declarations are LP64, and the Makefile uses generic bits headers when
there is no architecture-specific override. This gives offsets 0/4/8/12, 16,
17, 52, and 56, and total size 60—matching glibc's inspected public structure.
The shared managed LinuxTermios and raw-mode implementation therefore apply to
both libcs. There is no opaque oversized buffer or duplicated musl flag profile.

The public libc termios layout is not the smaller kernel TCGETS structure.
Musl tcgetattr/tcsetattr pass through TCGETS/TCSETS; their unused trailing public
fields are not independently synthesized as terminal state. The saved managed
structure starts zeroed and retains what the libc call returns.

Poll uses unsigned-long nfds_t in both libcs; read uses native-size size_t/ssize_t.
Winsize is four unsigned shorts (8 bytes), TIOCGWINSZ is 0x5413, and EINTR/EAGAIN
are 4/11. **ioctl differs:** musl declares `int request`, while glibc declares
`unsigned long request`. Separate source-generated declarations preserve those
signatures; only the size-query dispatcher branches on the selected libc.

## Setup and restoration

Capture validates stdin/stdout with libc `isatty`, requires stdin's foreground
process group, and snapshots the entire public termios structure. Exactly one
process-local NativeTerminal may own Unix stdin at a time. The caller must keep
the borrowed standard descriptors open and must not replace them during this
lifetime.

Raw input follows libuv `UV_TTY_MODE_RAW`, used by OpenTUI's JavaScript host:
clear BRKINT/ICRNL/INPCK/ISTRIP/IXON; enable ONLCR and CS8; clear
ECHO/ICANON/IEXTEN/ISIG; set VMIN=1 and VTIME=0. Control characters, including
Ctrl+C and Ctrl+Z, are bytes for the application to interpret, not tty-generated
signals. Other captured fields are retained. This deliberately does not use
`cfmakeraw`'s different raw-I/O output policy.

`tcsetattr(TCSANOW)` avoids an unbounded output-drain wait and does not discard
queued input. Snapshot get/set retries EINTR. Restoration is armed before the
first mode-setting call so setup failure still attempts restoration.

OpenTUI continues to own alternate-screen setup, capability queries, drawing,
and shutdown sequences. There is no ANSI renderer or replacement output engine
here. Dispose destroys the renderer first, then restores the saved termios even
when native destruction throws. Multiple cleanup failures are aggregated; setup
failure retains both setup and restoration failures. Disposal is one-shot and
releases the acquisition guard even when restoration reports an error. No GC
finalizer changes terminal state.

The Windows mode flags, Quick Edit restoration sequence, keyboard negotiation,
and existing renderer/resize/handle behavior are unchanged.

The cleanup recheck found no reason to add new flush/signal behavior. Native
`destroyRenderer` calls renderer destruction, then flushes input only when its
explicit flush_input argument is true. The existing managed owner passes false.
Native renderer destruction performs its output shutdown before backend/thread
teardown. Native suspend/resume only changes renderer terminal-output state; it
does not provide an OS termios/signal host. Those APIs are not substituted for
saved-mode restoration. Native output cleanup is best-effort inside a void API;
the wrapper does not claim it can surface errors that Zig catches internally.

## Exact host/input handoff

`NativeTerminal.InputKind` distinguishes `WindowsConsoleEvents` from `UnixBytes`.
For Unix, use the new method on the same terminal owner:

```csharp
bool ready = terminal.TryReadUnixInput(buffer, out int count);
// !ready: no input this frame (also yields on EINTR/EAGAIN).
// ready && count == 0: EOF/hangup; terminate the input lifetime.
// ready && count > 0: feed precisely buffer[..count] to an incremental decoder.
```

It polls with zero timeout and does not modify descriptor flags. With the
required exclusive reader and VMIN=1/VTIME=0, a readable descriptor can be drained
without waiting for another key. Do not run a second reader concurrently. Bound
per-frame drain work, preserve UTF-8 and escape-sequence fragments between reads,
and stop all reads/render callbacks before disposal. This method does not parse
keys, bracketed paste, mouse reports, terminal replies, or focus events.

**Do not substitute Console.ReadKey, Console.KeyAvailable, Console.In, or
Console.TreatControlCAsInput in this Unix ownership path.** The .NET Console PAL
maintains its own cached terminal modes and read preparation/restoration; those
APIs are not a coordinated raw-byte reader. This wrapper avoids even Console
redirection checks on Unix. The pinned .NET 11 preview source was subsequently
read with source fetching authorized: `dotnet/runtime` tag
`v11.0.0-preview.7.26381.103`, `src/native/libs/System.Native/pal_console.c`.
`SystemNative_StdinReady` calls `SystemNative_InitializeConsoleBeforeRead`;
`ConfigureTerminal` starts from `g_initTermios` and calls tcsetattr. Read
preparation, signal-for-break updates, and suspend/child-process paths share its
cached terminal state. Console polling is not a passive substitute for this
reader. No Console/PAL mode interoperability is claimed or required here.

The generic host owner must select the new UnixTerminalInput adapter and
coordinate shutdown before using this wrapper on Unix. Native
Kitty negotiation remains disabled until the host provides its corresponding
decoder. Automatic job-control suspend/resume, .NET Console signal-handler
coordination, forced termination, and uncatchable SIGKILL/SIGSTOP restoration
are not implemented or promised. This layer does not register process signals.

## Byte parser and dimensions handoff

`OpenTui.Blazor/UnixTerminalInput.cs` feeds the existing `TerminalInput` parser
through `FeedUnixBytes`, with a four-byte incremental UTF-8 prefix buffer. It
does not have its own escape interpreter. `ReadFrame(byteBudget: 4096,
readBudget: 4)` returns `UnixInputFrame` with `WouldBlock`, `BudgetExhausted`,
or `EndOfStream`, plus byte/read counts. It creates no streams or reader threads.
Do not call FlushEscape a second time on the Unix branch: ReadFrame does that
only after would-block, not when its budget expires. Incomplete UTF-8 delays
Escape timeout. EOF discards unfinished UTF-8/control strings and rejects an
unfinished paste instead of submitting partial text. Invalid UTF-8 is reported;
inside paste it rejects the whole paste rather than silently modifying content.

Host integration, on its existing dispatcher:

```csharp
// Unix bootstrap, before acquiring raw mode:
var size = NativeTerminal.GetUnixSize();
var terminal = new NativeTerminal(size.Width, size.Height);
// Construct the existing TerminalInput with its key/paste/response/focus/mouse
// callbacks, plus inputRejected: app.OnInputError when available.
var unixInput = new UnixTerminalInput(terminal, parser);
var frame = unixInput.ReadFrame();
// Process the callbacks already queued by this parser.
// EndOfStream ends input; BudgetExhausted yields to painting.
var resized = NativeTerminal.GetUnixSize();
// Compare cell size; call terminal.Resize and app.Resize only on a change.
// Dispose unixInput before terminal. The reader does not own the terminal.
```

`GetUnixSize` reports actual stdout cell and pixel dimensions with TIOCGWINSZ.
Zero cell dimensions and syscall errors fail explicitly; there is no fabricated
80x24 size or Console.WindowWidth fallback. Linux uses ioctl with request 0x5413.
Darwin uses the source-declared fixed-argument `__ioctl` shim from
libsystem_kernel with request 0x40087468 and the eight-byte winsize layout. The
macOS 11.3 SDK exports this shim for x64/arm64. It avoids calling public variadic
ioctl with an incorrect fixed-argument ABI on Apple arm64. This is an OS-specific
export, not a portable POSIX interface; missing symbols fail explicitly.

Keyboard coverage is the existing source profile: arrows/home/end/page keys,
Insert/Delete, F1-F12 CSI/SS3/Linux forms, rxvt shift/control forms, SS3 numeric
keypad, ESC-prefixed Alt, modifyOtherKeys, and simple CSI-u encoded printable
characters/special keys with explicit Shift/Alt/Control. Raw NUL is Ctrl+Space,
BS/DEL are plain Backspace, TAB/CR are Tab/Return, LF is a distinct Ctrl+J bridge
alias (ConsoleKeyInfo has no Linefeed member), and remaining C0 controls map to
logical control chords. Casing never invents Shift or a physical/base scan code.
Supplementary Unicode preserves both UTF-16 units, including Alt metadata.

Without a rich input consumer, enhanced Kitty/extended-modifier fields are not
silently downcast into plain key presses; the rich decoder and projection
contract are documented in `OpenTui.Blazor/TerminalKeyInput.md`. Arbitrary encoded
C0 keys, legacy eight-bit Meta, and unnegotiated UTF-8 mouse 1005 remain
unsupported. SGR/urxvt mouse uses the existing parser; raw X10 coordinates
bypass UTF-8 decoding based on that parser's Mouse state. Unknown sequences remain
opaque and are observable via unknownResponse/inputRejected and diagnostic state.
The existing cursorReportExpected callback disambiguates CPR from modified F3;
the legacy five-second startup heuristic remains when no callback is supplied.

## Source provenance and validation boundary

Source inspection used local production files and the source fetches listed below:

- OpenTUI `packages/native/src/renderer.zig` in `C:/Repos/opentui-ffi-buffer`,
  setupTerminal and performShutdownSequence; and the equivalent core Zig source
  in `C:/Repos/sst/opentui`.
- Installed OpenTUI 0.5.9 `chunk-bun-jxfx3h5k.js`, stdin.setRawMode(true) and its
  separate data listener. The Zig renderer does not set OS termios.
- `C:/Repos/sst/tmp-libuv/src/unix/tty.c`, uv_tty_set_mode and uv__tcsetattr.
  The local Node-vendored copy confirms the flag profile but uses TCSADRAIN;
  this wrapper explicitly selects the current libuv TCSANOW policy.
- Cached Rust libc 0.2.189 C-ABI declarations: `src/unix/mod.rs` (cc_t,
  size_t/ssize_t, pollfd, function signatures and Darwin symbol aliases),
  `unix/linux_like/mod.rs`, `linux/gnu/mod.rs`,
  `linux/gnu/b64/{x86_64,aarch64}/mod.rs`, and
  `unix/bsd/{mod.rs,apple/mod.rs}` (layouts, widths, flags, VMIN/VTIME and nfds_t).
  Linux poll takes native-width unsigned long; Darwin poll takes uint32.
  The Darwin $UNIX2003 symbol alternatives in those declarations are x86-only,
  not the supported x64/arm64 ABIs.

Additional source references:

- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/include/termios.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/arch/generic/bits/termios.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/arch/x86_64/bits/alltypes.h.in
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/arch/aarch64/bits/alltypes.h.in
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/include/alltypes.h.in
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/include/poll.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/include/sys/ioctl.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/arch/generic/bits/ioctl.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/arch/generic/bits/errno.h
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/src/termios/tcgetattr.c
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/src/termios/tcsetattr.c
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/src/misc/ioctl.c
- https://raw.githubusercontent.com/ifduyue/musl/v1.2.5/Makefile
- https://raw.githubusercontent.com/bminor/glibc/glibc-2.39/sysdeps/unix/sysv/linux/bits/termios.h
- https://raw.githubusercontent.com/bminor/glibc/glibc-2.39/sysdeps/unix/sysv/linux/bits/termios-struct.h
- https://raw.githubusercontent.com/bminor/glibc/glibc-2.39/misc/sys/ioctl.h
- https://raw.githubusercontent.com/dotnet/runtime/v11.0.0-preview.7.26381.103/src/libraries/Common/src/Interop/Unix/Interop.Libraries.cs
- https://raw.githubusercontent.com/dotnet/runtime/v11.0.0-preview.7.26381.103/src/coreclr/pal/src/loader/module.cpp
- https://raw.githubusercontent.com/dotnet/runtime/v11.0.0-preview.7.26381.103/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/RuntimeInformation.cs

- https://raw.githubusercontent.com/dotnet/runtime/v11.0.0-preview.7.26381.103/src/native/libs/System.Native/pal_console.c
- https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/libsyscall/wrappers/ioctl.c
- https://raw.githubusercontent.com/apple-oss-distributions/xnu/main/bsd/sys/ttycom.h
- https://raw.githubusercontent.com/phracker/MacOSX-SDKs/master/MacOSX11.3.sdk/usr/lib/system/libsystem_kernel.tbd
- https://raw.githubusercontent.com/torvalds/linux/master/include/uapi/asm-generic/ioctls.h
- Installed OpenTUI 0.5.9 parse.keypress.ts and StdinParser production code.

The LibraryImport declarations compile through the pinned local .NET 11 SDK.
No terminal/native/Unix execution, tests, PTY probes, application launches,
database/API/network probes, or process-management commands were used. Source ABI
matching and successful compilation are not runtime interoperability proof.

### libuv attribution

The raw-mode flag profile follows libuv, copyright (c) 2015-present libuv project
contributors, under its MIT license:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to
deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
