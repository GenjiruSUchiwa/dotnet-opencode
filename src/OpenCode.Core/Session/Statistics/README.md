# Session statistics read model

Ported source: `packages/core/src/session/stats.ts`,
`packages/schema/src/session-stats.ts`, `packages/protocol/src/groups/session.ts`
and the `session.stats` handler in `packages/server/src/handlers/session.ts`.

## API and composition

`GET /api/session/stats` accepts optional `from`, `to` (numeric epoch milliseconds),
`project` (Project.ID), `timezone` and `tools=none|summary|detail`. Result is
`{ data: SessionStats.Info }`. Defaults are current time, the earliest eligible
projection, UTC and summary, respectively. The existing Schema stats contract was
corrected from obsolete toolTotals to the source discriminated tools union, and
range timestamps now use the existing millisecond converters.

Core: `SessionStatistics.GetAsync(SessionStatisticsInput?, CancellationToken)`.
Client: `SessionHttpClient.StatsAsync(SessionStatsQuery?, CancellationToken)`.
Composition owner must register SessionStatistics with its existing IDatabase and
call `MapSessionStatsEndpoints()` under the existing authenticated pipeline.
No host/UI mapping, Skill endpoint, Job HTTP route, SessionStore or Event writer
was edited. The service uses IDatabase.CreateConnection and its readiness guard;
it neither opens a separately discovered database nor bypasses initialization.

The endpoint validates timezone and explicit from < to. Omitted from with no
eligible messages resolves to to: this is a valid empty aggregate, not an invalid
explicit equal range. Project with no matching facts also produces a real empty
aggregate. Database/SQL/IO failures are not caught and replaced by zeros or arrays.

## Projection counts and usage

Read windows are 31 * 24 hours in epoch time, with inclusive from/exclusive to.
Window boundaries do not represent local months or timezone midnights. Every
projection/tool query joins session_message to session_v2, filters the selected
project, and excludes inherited fork history where message.time_created is less
than the fork session's time_created.

- Only user and assistant rows identify active sessions/subagents in the range.
- parent_id NULL determines a top-level session; any non-null parent is a subagent.
- Prompts count only top-level user rows, never subagent users.
- Every eligible assistant projection counts one step, including unfinished ones.
  Stats does not apply archive isSettled filtering.
- Assistant tokens and cost come from actual message JSON fields, null-coalesced
  to zero. They do not come from Session totals, titles, event counts or guesses.
- Model grouping requires nonempty provider/model IDs and uses the exact source
  provider/model#variant key. Empty variant is omitted on the output reference.
  Missing model identity does not remove the step from global totals/activity.
- Models sort by the sum of input/output/reasoning/cache-read/cache-write tokens,
  descending. Ties preserve first observation order, as with source stable sorting.

## Tool reliability

none skips tool queries and returns only `{ mode: "none" }`. summary uses the
source MATERIALIZED CTE and SQL counts, returning mode and totals without usage.
detail reads tool name/status/duration, returning totals and per-name usage sorted
by descending calls (stable ties).

Only top-level assistant `content[]` items with type tool count. A CodeMode tool
entry counts once; nested executions in its payload, event log or text are not
recursively counted. completed means succeeded, error means failed; running,
streaming, cancelled, null and all other statuses are unfinished. Calls without
names still contribute to totals but not per-name usage.

Duration is completed - coalesce(ran, created) when completed exists, regardless
of status. Negative values are not clamped. P50 is the sorted middle value or
mean of the two middle values; durationP50 is omitted when no durations exist.
The service never parses tool output to guess success or cancellation.

## Timezones, DST and streaks

Each assistant instant is converted from UTC with TimeZoneInfo using the requested
IANA zone's rules, then formatted yyyy-MM-dd with invariant Gregorian formatting.
UTC is the default; invalid/unsupported zones fail, never fall back to local time.
There is no conversion of ambiguous local input: all input times are UTC epochs.
DST changes can create 23/25-hour days, but streaks compare DateOnly calendar-day
ordinals, not durations between timezone midnights. Only active dates are emitted;
no zero-count gaps are synthesized. Streak is the longest contiguous active run,
not necessarily one ending today.

Runtime timezone data comes from the platform/.NET ICU/tzdb integration, not Bun's
Intl installation. IANA names and UTC are supported; raw offset timezone strings
are not supported by this adapter. Historical tzdb-version differences and aliases
remain unverified. No DST execution/probes were performed.

## Compaction overhead and a source discrepancy

The source enumerates all Session IDs in the project, batches them by 500, and
queries durable events with source=compaction and created in the requested range.
Successfully schema-decoded usage adds only to total tokens/cost, not sessions,
steps, models or active dates. Invalid usage payloads are skipped, as with source
decodeUnknownOption; malformed JSON that fails the SQL query is not hidden.
Title usage is excluded. An implicit empty message range is empty even if earlier
compaction-only records exist, matching source range derivation.

**The query uses `SessionEvent.UsageRecorded.type`, the unversioned
`session.usage.recorded`, exactly as stats.ts does.** Both the TypeScript Bus writer
and .NET EventStore store version-suffixed names such as session.usage.recorded.1.
Consequently those normal versioned records do not contribute through this source
query. No undocumented version fallback or event rewrite was added. Correcting
this requires an explicit source/port behavior decision; this port does not claim
that versioned compaction overhead is included.

## Limits and validation

- Finite decimal/scientific numeric query syntax is supported, including fractional
  filter boundaries. Bounds are not restricted to nonnegative timestamps.
- .NET date output supports years 1–9999. Larger ECMAScript calendar ranges and
  exotic NumberFromString forms are not supported. Output epoch milliseconds use
  truncation of fractional milliseconds, while SQL filters retain numeric bounds.
- Existing stats count contracts use Int32. Increment/count overflow fails instead
  of wrapping or returning fabricated totals. Broader JS integer count ranges are
  not claimed supported.
- Queries are sequential per source 31-day windows. Compaction batches are executed
  sequentially here instead of source concurrency four; the selection and batch
  reduction order are retained. No global read snapshot is promised, as upstream
  also queries multiple windows without one transaction.
- Message rows are streamed from the reader. Model/day/session maps, the Session ID
  list and detailed durations are retained in memory; this is not constant-memory
  processing of arbitrarily large detail requests.

## Build evidence

Pinned .NET 11 isolated Client/Protocol build and the final full Core/Server build
both passed with zero warnings/errors. The earlier other-owned
Event/SessionArchivePersistence.cs line 76 blocker cleared before final validation;
that file was not edited here. No tests, runtime stats requests, SQL/database execution, production reads,
filesystem/network probes, native/app execution, commits or delegation occurred.
Build-only evidence is not runtime or timezone conformance verification.

## Source license

Derived from OpenCode, MIT License, copyright (c) 2025 opencode.
Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the Software), to deal in the
Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies, and to
permit persons to whom the Software is furnished to do so, subject to inclusion
of this copyright and permission notice in all copies or substantial portions.
THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE
AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR ITS USE.
