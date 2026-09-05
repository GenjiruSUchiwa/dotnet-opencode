# System.CommandLine migration

## Scope and entrypoint

The entire implemented CLI surface is now one `RootCommand` tree in
`CliApplication.CreateRoot()`. Program.cs only sets UTF-8 output and invokes that
tree. No command handler receives or scans argv.

| Implemented command | Typed action input / handler |
| --- | --- |
| root / tui | TuiOptions → LifecycleCommands.TuiAsync |
| run | RunOptions → RunCommand.RunAsync |
| stats | StatisticsOptions → StatisticsCommand.RunAsync |
| api | ApiOptions → ApiCommand.RunAsync |
| auth login | AuthLoginOptions → AuthCommands.RunAsync |
| console login | AuthLoginOptions with Console=true → AuthCommands.RunAsync |
| serve | ServeOptions → LifecycleCommands.ServeAsync |
| status | LifecycleCommands.StatusAsync |
| stop | LifecycleCommands.StopAsync |

Auth/console groups without a selected subcommand are errors, not successful
placeholders. No unsupported auth list/logout, generic service command group,
Job route, export/import command or model-execution shortcut was invented.
Unsupported remote/private routing for auth/console/serve/status/stop fails in
tree validation, even though the shared connection flags are available globally.

Removed: Program's command/options loop, RunOptions.Parse, ApiOptions.Parse,
StatisticsCommand.Parse, AuthCommands' argv scanner, and duplicated manual help
constants. Existing workflow methods now receive only typed inputs. JSON/body
resolution and semantic operations are not re-parsed as command lines.

## Stable package and official API evidence

Pinned **System.CommandLine 2.0.11**, the latest non-preview version listed by the
official NuGet flat-container metadata at inspection time. Version 3 entries were
explicitly previews and were not selected. The package is Microsoft-authored,
MIT-licensed, and supplies a net8.0 asset compatible with the pinned net11.0 target.
The .NET SDK remains 11.0.100-preview.7.26381.103; no novelty SDK/package upgrade
was performed.

Inspected official metadata/source:

- https://api.nuget.org/v3-flatcontainer/system.commandline/index.json
- https://api.nuget.org/v3-flatcontainer/system.commandline/2.0.11/system.commandline.nuspec
- NuGet repository commit `e2f47b0110ed922f21a1522da67279133ce28f32` in dotnet/dotnet,
  src/command-line-api/src/System.CommandLine: Command, Option<T>, RootCommand,
  ParserConfiguration, ArgumentArity, ParseOperation, ArgumentResult, OptionResult,
  Token/StringExtensions and action types.
- Microsoft Learn syntax and migration/API documentation. Implementation follows
  the actual stable source, not obsolete beta4 SetHandler/CommandLineBuilder APIs.

Source grammar/coercion was checked against the installed Effect 4.0.0-rc.112
CLI lexer/parser/Param/Primitive and Config/Schema number/boolean definitions.

## Parsing, coercion and safety

System.CommandLine owns symbols, aliases, arities, typed value conversion,
validators, help, command binding and asynchronous invocation. `--` terminates
option interpretation and its remaining strings flow through the command's real
arguments. ResponseFileTokenReplacer is explicitly null: @file remains literal
in API data, run input, filenames and all other arguments. Default directives are
removed and directive tokens are rejected; no implicit environment/response-file
operation can run during parsing/help.

The public stable lexer has a few source incompatibilities. The narrow
`CliBooleanSyntax` shim is derived from the actual tree, not a copy/wrapper of any
old command parser. It does not bind actions or construct command DTOs. It:

- Preserves former Program command-name casing by canonicalizing recognized tree
  command names only, never values of string options or text after --.
- Maps the source lowercase boolean literals true/yes/on/1/y and
  false/no/off/0/n to the library's true/false tokens. Implicit flags stay true;
  absent flags use their typed false defaults. In particular --auto=no cannot
  accidentally become an implicit approval plus a message word.
- Implements source --no-name forms and rejects a value on a negated flag.
- Preserves source short-cluster behavior rather than accepting arbitrary suffixes
  as string values. Unknown aliases and required-valued flags inside a cluster
  fail instead of becoming a prompt or attached value.
- Preserves explicit empty values such as --data= and --title=, which stable
  2.0.11 otherwise drops while splitting inline option tokens.
- Rejects the library-only colon option delimiter. Colons inside actual header,
  URL or data values remain literal and are not normalized.

A post-parse check of command-argument tokens rejects unknown option-shaped words
before --. Otherwise System.CommandLine can absorb those words into run's variadic
message argument, regressing to sending misspelled flags to the model. Tokens
bound to actual string options are not subject to that check.

Typed value parsers preserve source rules:

- Scalar flags select their first occurrence, matching Effect Param.parseFlag.
- Files/headers/params consume one value per repeated flag, not greedy batches.
  Files and headers have the source maximum of 100. The earlier ad-hoc API parser's
  unverified consecutive key=value expansion is removed.
- Param entries require exactly one '=' and nonempty key/value; duplicate keys
  replace the previous value. Headers split at the first colon, use source trim
  whitespace, and retain last-value-wins case-insensitive semantics.
- Stats integer values use source Number-style decimal/scientific/radix coercion,
  integer checks and the declared bounds. No locale-dependent thousands syntax
  or non-finite value is accepted. Existing native Int64/calendar limits remain.
- API data is always a literal string: no JSON parsing, @file expansion, or stdin
  body interpretation occurs in the command tree. Runtime transport semantics
  (including HTTP error bodies and source status exit behavior) are unchanged.

### Remaining managed-parser boundary

System.CommandLine treats a following option-shaped token as a string option's
value in some cases where the source lexer would treat it as another flag. Use
`--data=...` (and analogous explicit value syntax) to remove that ambiguity.
There is no second general-purpose argv parser to emulate all undocumented lexer
error-ordering behavior. Help layout is now generated by System.CommandLine,
not byte-identical to the removed hand-written usage strings. These differences
are explicit; no runtime parser-conformance claim is made from compilation.

## Help, output and lifetimes

Help and version are terminating tree actions and cannot enter DI, read stdin,
create a host or resolve a server. Only --help/-h aliases are exposed for help;
version prints the native service/application version, not the parser package
version. Parse diagnostics go to stderr without typo suggestions or help appended
to stdout. Parse errors return 1 for run/stats/api and 2 for the other native
command boundaries. Business-handler exit codes and output streams are retained.

InvocationConfiguration.ProcessTerminationTimeout is null: the parser library's
default two-second process-termination timeout is disabled. Run keeps its
first-Ctrl+C cooperative cancellation and second-Ctrl+C immediate exit 130;
Stats/API/Auth receive their existing cancellation tokens. Serve keeps host
ConsoleLifetime and the TUI keeps its native cancellation/key behavior. No fixed
run deadline, greeting, extra LLM loop or embedded SDK prompt shortcut returns.

The same private lease/client ordering is preserved for root TUI, tui, run, stats
and api. --server/--standalone exclusion is validated before action invocation.
No managed registration/election/recovery rights are added. Auth remains explicit
local-channel login, and headless --auto/aliases remain separate opt-in booleans
combined only after typed parsing. Forms are not automatically answered.

## Serve's external public API boundary

All native Serve options are in the tree, including port/-p, registration-file,
service-config and hidden service/startup diagnostic flags. Only validated typed
values are translated into ServerHost.CreateApp's existing string-array contract.
Original argv is never forwarded or parsed again by CLI code. ServerHost's own
parser is outside this ownership; a future typed Server factory would remove
that final serialization adapter. No Server/Core/Client/Schema file was edited
to bypass that boundary.

## Vogen call sites

CLI directly references **Vogen 8.0.7** with all analyzers enabled. ID construction
uses the Schema owner's preserved Create()/FromExisting APIs, including PluginId,
SnapshotId and SkillId after their source-nonnull migration. Replacements in Razor
code-behind and the tool-view expression are mechanical;
there are no layout/style/data-model redesigns. Static factory calls are qualified
where a SessionId property shadows the type name. The agent picker's absence
sentinel is now AgentId? rather than GetValueOrDefault() creating an uninitialized
Vogen ID. Skill and inbox selectors similarly use nullable IDs for absent current
selection. External selection callbacks and rendered behavior are retained.
Validation boundaries check IsInitialized() before reading a possibly uninitialized
ID; empty/whitespace Skill IDs are not repurposed as absence or newly rejected.
No Vogen analyzer is suppressed. Terminal transport-controller framing and all
non-CLI conversion work remain with their respective owners and were not edited.

## Verification

Only official package/source inspection, restore, source audits and pinned .NET
11 full-CLI builds with isolated artifacts and OpenApiGenerateDocuments=false are
permitted. No test command, sample args, parser invocation, help invocation, CLI,
standalone host, DI startup, HTTP/native/DB/model or process behavior was executed.
The integrated build includes Schema, Protocol, Core, Client, Server, SDK and TUI
dependencies; earlier concurrent non-CLI Vogen errors were not worked around by
excluding projects or suppressing analyzers. The final integrated build after the
CLI Vogen reference and last-three-ID/nullability audit passed with **0 warnings
and 0 errors**. Artifacts: `C:/tmp/opencode/commandline-cli-build`. Compilation and
source inspection are the only evidence; no command-tree invocation or runtime
UI/serialization conformance was tested.
