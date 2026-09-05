# Statistics CLI

Parsing/help now use the complete shared System.CommandLine tree. StatisticsCommand
receives typed StatisticsOptions; its manual parser/help scanner was removed.
See CommandLine/README.md for package evidence and lexical compatibility details.

Source: `packages/cli/src/commands/commands.ts` stats specification and
`packages/cli/src/commands/handlers/stats.ts`, plus server-connection.ts.

## Dispatch and flags

Program.cs has only a targeted stats branch and usage-line addition. The command
does not enter InteractiveTui, the SDK Ask loop, Core SQL, or an official/Bun
launcher. A future explicit invocation resolves the .NET service through
ServiceDaemon and performs typed SessionHttpClient.StatsAsync requests.
No command/service/HTTP execution was used for verification.

Recognized source options: --days, --year, --all, --project, --models, --tools,
--cost, --full, --limit, --json, --server, --standalone, plus --help/-h.
There are no invented --from, --to, --timezone, --format or --tools=detail flags.
--tools is a boolean; mode is derived below. Value options support --name=value
and --name value; booleans support --name and --name=true|false. Unknown/repeated
options are handled by the shared tree. Unknown options fail; repeated scalar
options use the source first occurrence. Source boolean literals/negation are
supported without inventing short aliases.

- days is an integer >= 0; 0/1 mean today.
- year is an integer 1970–9999, defaulting to the current local year.
- days/year/all are mutually exclusive.
- limit defaults to 5 and must be an integer >= 1. It limits displayed detail
  rows only, not the requested aggregate or JSON output.
- --server and --standalone conflict before connection.
- Help returns 0 without connecting. Successful output returns 0. Command/API/
  transport/timeout/cancellation errors go to stderr and return 1. Program's other
  command exit handling is unchanged. No error becomes fabricated zero activity.

## Request semantics

Ranges use one captured current instant after connection. End is now+1ms.
Default/current-year starts at local Jan 1 and is labeled "YEAR so far". Other
years end at the following local Jan 1. --all omits from and uses "all time".
--days starts at local midnight minus max(0,N-1) calendar days, not N*24 hours.
Local gap/fold resolution follows JS Date's advance-by-gap/earlier-fold policy.
Timezone sent to stats is the system zone converted to an IANA ID, never a guessed
fixed offset. Unmappable zones fail explicitly rather than silently use UTC.

Tool mode follows source exactly:

| Options | API tools mode |
| --- | --- |
| --json, --tools, or --full | detail |
| Otherwise --models or --cost | none |
| Default report | summary |

--project ID sends that ID. --project . calls the existing typed CurrentProjectAsync
with cwd. That native endpoint uses the shared server RequestLocation/project
resolver; source uses location.get to obtain the same project identity. No project
ID is guessed locally, and failure does not fall back to all projects. Each project
or stats request has its own 30-second deadline, linked to cancellation.

--json prints the indented SessionStats.Info itself, not the HTTP {data} envelope,
matching the upstream client's unwrapped return. JSON wins over text section
rendering. No tables, terminal escapes or invented summary fields are mixed into it.

## Output

The source heading, scope/range labels, empty-activity report, calendar glyphs,
month labels, weekday spacing, legend and footer are retained. Calendar buckets
are interpreted in the selected local timezone. The width-dependent crop copies
the source algorithm, including its Sunday-based cropped starting point; it is
not silently "corrected" into a different heatmap layout.

Detailed sections render in source order: cost/tokens, models, tool reliability.
Widths below 68 use stacked rows; wide tables retain source column widths.
The API's stable model/tool ordering is retained. Row limits and +N-more lines,
finished/unfinished calls, success/error denominators, cache-input ratio and p50
formatting follow the source. Empty requested detail sections do not become a
default activity report.

Numbers use k/m/b abbreviations, en-US grouping, source percentage precision and
ms/seconds durations. The fixed-decimal formatter rounds the exact IEEE double
value like JS toFixed rather than .NET midpoint-even formatting. Huge exponential
representations retain the platform's general-format behavior.

When stdout is a terminal and NO_COLOR is absent, the report uses the source SGR
styles and COLORFGBG-selected colors. Redirected/NO_COLOR output contains the same
plain glyphs and text without SGR. This is one-shot CLI styling, not an ANSI TUI:
no alternate screen, input loop, cursor painting or interactive stats dialog exists.

## Explicit limitations and lifecycle policy

- **--standalone uses StandaloneHostLease**, backed by the Server-owned private
  composition. Its ephemeral loopback endpoint uses the same typed HTTP requests;
  client disposal precedes owned host shutdown. There is no managed registration,
  election, recovery, or shared-service fallback. See Hosting/StandaloneHost/README.md
  for lifecycle and TUI handoff details. No standalone runtime verification occurred.
- Managed service discovery/start uses the existing .NET channel/version/build
  policy, which is stricter than source stats' ignore-version managed mode.
  Explicit servers use ServiceDaemon.InspectAsync without version rejection,
  warn on a version mismatch and are never started/replaced by this command.
  Native health-probe timing remains the lifecycle's policy, not a new retry loop.
- Explicit-server password uses OPENCODE_DOTNET_SERVER_PASSWORD, consistent with
  the native CLI, rather than reading another channel's registration/credentials.
- Local calendar/tzdb behavior is provided by .NET and has not been runtime-tested.
  Year 9999 is accepted by the source flag contract, but an end at year 10000 and
  extreme day ranges exceed the current .NET/API calendar. They fail explicitly;
  ranges are not clipped or relabeled. Integer flags use Int64 parsing.
- The API's existing stats limitation is preserved: its compaction query uses
  unversioned session.usage.recorded while writers store versioned event names.
  The CLI reports returned totals and does not silently add estimated overhead.

## Verification

The final isolated pinned .NET 11 full-CLI build passed with zero warnings and
zero errors, with OpenApiGenerateDocuments=false and local package restore sources.
The first attempt was blocked in other-owned Core/Tools/ShellSyntaxScanner.cs at
lines 236 and 255; those errors cleared before the final build without edits here.
No tests, Program/stats/service execution, API/SSE requests, database/SQL access,
native execution, Git commands or filesystem/network probes were performed.
Build success, when available, is not runtime/output parity evidence.

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
