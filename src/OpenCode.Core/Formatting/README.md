# Local Formatting

`LocalFormatter` implements the local builtin/configured-command portion of
`core/formatter.ts`, `formatter/builtins.ts`, and `config/plugin/formatter.ts`.
`ToolLocationFactory` supplies it when no custom formatter adapter is provided.
Nothing is registered as a shell tool, and there is no shell or package-install fallback.

## Approved Ordering

`IToolFileFormatter.PrepareAsync(absolutePath, ct)` reads actual ConfigLoader
configuration and captures matching definitions while the mutation lock is held.
It validates unsupported configuration before writing and never executes commands.
Write/edit then read their snapshot, build the preview and obtain leaf `edit`
approval. Only after the snapshot check and file write does
`IToolFileFormatPlan.ApplyAsync(ct)` discover/run executables or probe `air`/`uv`.

The mutation retains its lock through formatting and final readback. Successful
formatting restores the original/caller-requested UTF-8 BOM. Failed formatters may
have partially changed the file; final content is reread even when no formatter
succeeds, so edit output does not falsely describe the pre-format version. This is
not rollback or an atomic transaction with external writers.

## Configuration

Missing/false `formatter` means no formatters. True installs the source builtin
order. A map installs that order, then applies entries by name: disabled removes,
existing-name replacement preserves position, and new names append. `extensions`
and `command` replace inherited values; `environment` merges inherited values.
Missing command inherits builtin detection or disables an unknown formatter.

Configuration is reread per preparation. Successful command discovery is retained
while that formatter configuration snapshot is unchanged; unavailable commands are
not cached. Matching uses exact case-sensitive final extensions as in `path.extname`.
Only the first `$FILE` in each argument is replaced. Commands use argv, Location cwd,
inherited plus configured environment, ignored stdin and drained output. Exit zero
stops the chain; expected spawn/IO errors or nonzero exits are logged and try the
next formatter. Defects and interruption are not converted to success.

## Native Commands

| Name | Command After Discovery |
| --- | --- |
| gofmt | `gofmt -w $FILE` |
| mix | `mix format $FILE` |
| zig | `zig fmt $FILE` |
| clang-format | `clang-format -i $FILE` |
| ktlint | `ktlint -F $FILE` |
| ruff | `ruff format $FILE` |
| air | `air format $FILE` |
| uv | `uv format -- $FILE` |
| rubocop | `rubocop --autocorrect $FILE` |
| standardrb | `standardrb --fix $FILE` |
| htmlbeautifier | `htmlbeautifier $FILE` |
| dart | `dart format $FILE` |
| ocamlformat | `ocamlformat -i $FILE` |
| terraform | `terraform fmt $FILE` |
| latexindent | `latexindent -w -s $FILE` |
| gleam | `gleam format $FILE` |
| shfmt | `shfmt -w $FILE` |
| nixfmt | `nixfmt $FILE` |
| rustfmt | `rustfmt $FILE` |
| pint | `./vendor/bin/pint $FILE` |
| ormolu | `ormolu -i $FILE` |
| cljfmt | `cljfmt fix --quiet $FILE` |
| dfmt | `dfmt -i $FILE` |

Extensions and fallback order are copied from source. Clang and OCaml require their
upward marker; ruff checks the source config/dependency files and text markers. Pint
requires `laravel/pint` in composer require/require-dev. Air validates the first
`--help` line for `R language` and `formatter`; uv requires `format --help` exit zero.
Probes execute only in the approved post-write phase. Native executable discovery
uses PATH/PATHEXT and optional `LocalToolOptions.FormatterBin` appended after PATH.

Configured command arrays are supported, including overrides of builtin names.
Use a real executable or an explicitly configured interpreter for scripts. Windows
batch/npm shims are not silently routed through an implicit shell.

## Explicit Limits

Automatic oxfmt/prettier/biome require the unported native `Npm.which` package/cache/
installation lifecycle. Their source names/extensions/environment remain available
for configured overrides, but matching automatic definitions fail preparation
before mutation. Configure an explicit command or disable each matching unsupported
definition. This is conservative even when current package metadata would disable
one: writing package metadata can itself make it eligible after the write.

No npm installer or guessed project-local binary stands in for the source service.
Plugin formatter transforms and the general Config observer lifecycle are not
implemented. Unknown entry fields and empty commands are rejected rather than guessed.
Discovery text files have a 1 Mi-character limit; air probe capture is 8192 characters.
Processes have a local 120-second timeout and owned child/reader settlement. These
limits are explicit safety extensions, not claims of byte-for-byte source parity.

The .NET 11 adapter now uses null standard handles for ordinary formatter output
and input, empty explicit inherited-handle lists, guarded KillOnParentExit, and a
SafeProcessHandle cancellation wait. Help probes keep bounded chunk capture rather
than adopting unbounded ReadAllText/ReadAllLines. See `../Tools/PROCESS-NET11.md` for
root/tree cancellation ordering and platform guarantees.

Verification was isolated builds only. No formatter, probe, config discovery,
filesystem mutation, package installation, application or tests were executed.
