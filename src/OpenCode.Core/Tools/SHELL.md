# Local foreground shell

`ToolLocationFactory` registers `shell` through the same ordered builtin transform
as the file tools. It captures `LocalShellPolicy`, the Location's actual permission
service, and `ShellProcessSource`. Registry visibility is not execution authorization.
`LocalLiteralShellPolicy` remains an explicit legacy adapter; the factory does not use it.

## Selection and permission order

1. Select an executable without running discovery commands. An optional absolute
   `LocalToolOptions.ShellExecutable` takes priority, followed by the current merged
   `shell` configuration, then a supported `SHELL`, then platform fallbacks.
2. Scan the complete command before any approval or process creation. Retain raw
   resource spans for every command, including commands inside `$(...)`.
3. Resolve the invocation directory and explicit directory-change arguments through
   `LocalToolLocation`. Assert deduplicated `external_directory` resources and saves.
4. Assert all non-directory command resources using action `shell`, with upstream
   prefix arities. Redirected directory commands also require shell approval because
   they have a file side effect. If a normalized prefix cannot match the raw command,
   save its source spelling instead of an unusable prefix.
5. Recheck the working directory and executable after approval, then start the process.

Permissions use the invocation's Session, Agent, Message and call IDs. File leaves,
shell, skill and webfetch translate `PermissionBlockedException` into an explicit
`ToolExecutionException` containing action, resources and applicable rules. Correction
feedback is also a leaf failure. Bare user decline and cancellation are not caught.
The permission service itself preserves these distinct exception types, including for
non-tool callers. There is no registry-wide authorization or permission-error blanket.

## Supported grammar

The native scanner is a documented subset, not a full tree-sitter or portable-scanner
port. The selected shell executes the original, unchanged string after scanning.

- Shell families: PowerShell (`pwsh`, `powershell`) and POSIX-compatible `bash`,
  `dash`, `ksh`, `sh`, `zsh`. Actual command/operator availability still depends on
  the installed shell version; for example, Windows PowerShell 5 rejects `&&`.
- Literal command names and arguments, including quoted executable paths. PowerShell
  quoted executable paths require the `&` call operator.
- Single and double quotes; POSIX concatenated quoting; PowerShell doubled quote
  escapes. PowerShell concatenated quoting is rejected.
- POSIX backslash and PowerShell backtick escapes, including line continuations.
  PowerShell continuations are supported only between tokens, not inside words or
  strings. POSIX commands require LF newlines; PowerShell accepts LF, CR and CRLF.
  PowerShell Unicode escapes and typographic quote/dash syntax are rejected.
- Simple variables in arguments (`$NAME`, `${NAME}`, plus PowerShell `$env:NAME`),
  with evaluation left to the shell. Variables cannot supply command names or cwd.
- Recursive `$(command)` substitutions in arguments and double quotes. All contained
  commands receive permission analysis. Nesting is limited to 32 levels and command
  input is limited to 65,536 UTF-16 characters.
- `|`, `&&`, `||`, semicolon-separated and newline-separated command lists, plus
  token-boundary `#` line comments. The scanner rejects incomplete operators.
- POSIX `<`, `>`, `>>`, numeric stream descriptors and `<&`/`>&` duplication or
  closing. PowerShell `>`, `>>`, stream descriptors `1`–`6` or `*`, and `>&1` merges.
  PowerShell redirects must start at a token boundary. Redirect-only statements,
  `&>`, `&>>`, `>|`, `<>`, `|&`, and here-documents/here-strings are not supported.
- `cd`, `chdir`, `pushd`, `set-location`, and `push-location` with one literal path;
  PowerShell also accepts separate `-Path` or `-LiteralPath`. No glob, variable,
  substitution, implicit home, named-user home, stack restoration or multi-path cwd.
  Explicit `~` and `~/...` use the Location's home resolution.
- POSIX ordinary argument globbing is left to the shell. Compound brace/extended
  glob syntax is not supported. PowerShell array and splat syntax must not be used.

Compound statements, control-flow keywords (including return/throw/exit), loops, functions, assignments/declarations, grouping, script
blocks, arithmetic, parameter operators, backtick command substitution, stop-parsing
`--%`, and dynamic command names fail before execution. Use a separate tool call or
an explicitly authorized executable/script for work outside the grammar.

The scanner is not a sandbox and does not inspect script contents or programs invoked
by a command. As upstream does, directory arguments resolve lexically against the
initial invocation cwd, not by evaluating a shell's evolving state. Ordinary file
arguments and redirect targets are covered by shell command permission, not separate
file-tool permissions. Symlinks, shell aliases and executable behavior do not become
an OS access-control boundary.

## Native process source

`ShellProcessSource` uses .NET 11 `File.OpenNullHandle`, explicit standard handles,
`InheritedHandles = []`, `KillOnParentExit` on Windows/Linux, and the owned
`SafeProcessHandle.WaitForExitOrKillOnCancellationAsync`. It never reopens a PID.
Timeout/cancellation stops the owned tree before the managed root kill/wait settles.
Caller cancellation propagates; a deadline returns a completed timeout result.
The default foreground timeout is 120,000 ms, and zero disables it.

Stdout and stderr share one native file handle. This avoids split-stream ordering
and pipe-EOF waits. The returned snapshot retains the last 50 KiB and 2,000 lines,
with a Unicode-safe leading UTF-8 boundary. Truncated output names its full capture
file. File metadata is returned for every capture. Normal nonzero exits remain tool
results, not fatal attempts. Capture uses `shell/<project-id>/sh_<random>.out` under
the configured data directory, or an absolute `ShellOutputDirectory` supplied by the
host. `TERM=xterm-256color` and `OPENCODE_TERMINAL=1` are set.

### Session environment handoff

Set `LocalToolOptions.ShellEnvironment` to the host's existing
`SessionEnvironment.Get` method. Its type is
`Func<SessionId, IReadOnlyDictionary<string, string>?>`. The factory only supports
implicit-local placement, matching the source's environment-replacement boundary.
The callback receives the canonical tool Session ID, not a model-supplied value.

The leaf snapshots the returned map before permission preparation. Null inherits
the host environment. A non-null map replaces it completely, including an empty map:
`ShellProcessSource` clears `ProcessStartInfo.Environment` before copying values.
The two terminal markers above are then applied. No process-global environment is
changed, and later Session updates do not mutate an already-prepared invocation.
Executable selection still uses the host/config selection policy, as upstream does.
Session prompt admission and ownership of the environment map remain with Core Session.

## Remaining differences

- No background jobs, `background: true`, bare `&` background syntax, promotion,
  managed shell IDs/status API, progress publication, completion notification,
  recovery, or PluginRuntime job lifecycle. Programs can still spawn descendants;
  escaped/orphaned descendants are not a cross-platform containment guarantee.
- No shell-create hooks or hook-driven timeout changes. Session-specific environment
  replacement requires the real host callback above; an omitted callback inherits
  the host environment rather than synthesizing a Session environment.
- No output retention sweep or disk quota. Full captures remain on disk, including
  partial captures from cancelled calls. Tail limits are fixed, not `tool_output`
  configuration driven. Long-running output can consume disk space.
- Selection is not a full `ShellSelect` port. `cmd`, fish and nu are rejected; an
  unsupported configured shell is an explicit error rather than a compatibility
  fallback. Windows searches PATH for PowerShell/bash, then Git's sibling bash and
  the system Windows PowerShell path. No configurable Git Bash/global-bin override.
- Permission spans include each command's redirects; exact tree-sitter span quirks
  and the upstream PowerShell special prefix algorithm are not claimed equivalent.

Verification is build-only with the repository-local .NET 11 SDK and isolated
artifacts. No shell, process adapter, application, provider, database or test was run.
