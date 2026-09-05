# .NET 11 Transient Process Ownership

Search/Formatting sites share `OwnedToolProcess`. Foreground shell now uses the
file-backed `ShellProcessSource` described in `SHELL.md`, with the same .NET 11
ownership APIs and no redirected reader tasks. SDK installation, global.json,
TFMs and daemon/service process creation remain owned elsewhere. No compatibility
reflection, conditional .NET 10 fallback, process-ID lookup or daemon termination
policy is added here.

## Verified APIs

The Microsoft article and dotnet/runtime reference/source confirm:

- `File.OpenNullHandle()` returns a non-inheritable `SafeFileHandle` suitable for
  `ProcessStartInfo.StandardInputHandle`, returning EOF rather than sharing stdin.
- `ProcessStartInfo.InheritedHandles = []` permits only standard handles, limiting
  accidental inheritance from concurrently launched sibling processes.
- `ProcessStartInfo.KillOnParentExit` is supported on Windows and Linux (also Android
  in runtime); this implementation enables it only under Windows/Linux guards.
- `Process.SafeHandle.WaitForExitOrKillOnCancellationAsync(token)` returns
  `ProcessExitStatus`, including `ExitCode`, `Signal`, and `Canceled`.

References:

- https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/
- https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/ref/System.Diagnostics.Process.cs
- https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/Microsoft/Win32/SafeHandles/SafeProcessHandle.cs

The shared adapter now uses these APIs. It keeps the `Process` object because it
still needs bounded stream readers and `Kill(entireProcessTree: true)`. It borrows
the owned process's SafeHandle, rather than reopening a PID. The old stdin pipe,
immediate writer-close, and Process.WaitForExitAsync cleanup are removed.

## Cancellation and Drain Ordering

The managed cancellation wait kills the direct child, not its descendant tree.
Wiring caller cancellation directly to it would kill the root before tree traversal
could reliably identify its descendants. Instead:

1. Reader/wait timeout and caller cancellation use the existing lifetime token.
2. The managed SafeHandle wait starts with a separate stop token.
3. Early search cutoff, timeout, cancellation or failure first requests termination
   of the owned tree while the root is still identifiable.
4. The stop token then lets the managed API kill/reap the root if necessary.
5. Both bounded reader tasks are cancelled and observed before disposal completes.

Only the tree-specific termination remains custom. Root waiting/reaping belongs to
the managed API. Cleanup continues through managed settlement and reader observation
even if the tree termination request fails.

`status.Canceled` is not treated as a nonzero exit code or automatically as timeout.
An internal search cutoff may produce a canceled status and is still an expected
truncated-search completion. Actual caller cancellation remains cancellation; the
configured deadline remains timeout. External termination alone is not inferred to
be user cancellation from an exit code. Timeout zero still disables the deadline.

## Output Capture

Ripgrep output retains fixed-size chunk reads, bounded stderr and search record
limits. Shell captures combined native streams to a file and reads a bounded tail
after root settlement; see `SHELL.md`. Formatter help probes also
retain bounded chunk capture: an executable named air/uv does not guarantee finite
output or a finite line length.

No current capture site qualifies for unconditional ReadAllTextAsync or
ReadAllLinesAsync. Those APIs would accumulate a complete output/line before the
existing limits can apply. They were not substituted merely to demonstrate the API.

Normal formatter execution ignores stdout/stderr in the source. It now sends both
to the same null device via StandardOutputHandle/StandardErrorHandle and starts no
pipe readers. Its exit code still uses the same managed wait and tree-stop ordering.
Help probes alone request capture. All these operations remain noninteractive:
shell.ts also explicitly spawns the tool shell with stdin `ignore`.

## Platform Limits

InheritedHandles excludes unintended sibling handles, not grandchildren that a child
chooses to spawn with inherited standard handles. Bounded readers therefore remain
cancellable even after root exit; they are not replaced by an assumption of EOF.

Windows KillOnParentExit uses a job and includes descendants still in that job.
Linux parent-death behavior targets the direct child; it is not a descendant-group
guarantee. macOS/other platforms do not enable the unsupported flag. Escaped or
already-orphaned descendants cannot be retroactively identified once the root exits;
this migration does not claim a new cross-platform containment guarantee.

These flags are only for transient tool/formatter children. They must never be copied
to a daemon intended to outlive its launcher. Shell parsing now has an explicit
supported native subset; background lifecycle remains unsupported.

Verification is reference inspection and isolated dependency/build operations only;
no child process, formatter, shell, search adapter, test or application was invoked.
