# Durable log, replay, and replay ownership

`OpenCode.Core.Event.Log.DurableEventLog(IDatabase, pageSize: 512)` exposes:

```csharp
IAsyncEnumerable<DurableLogItem> LogAsync(string aggregateId, double after = -1,
    bool follow = false, CancellationToken ct = default);
Task ReplayAsync(SerializedDurableEvent input, DurableReplayOptions? options = null,
    CancellationToken ct = default);
Task ClaimAsync(string aggregateId, string ownerId, CancellationToken ct = default);
```

`SessionStore.LogAsync(SessionId, after?, follow, ct)` adds the source Session
existence check. SDK `LogAsync` delegates to it and links follow cancellation to
the owned host lifetime. Replay/claim remain explicit Core replication APIs, not
new public HTTP operations or features added to the volatile `/api/event` feed.

## Log semantics

Source: `core/bus.ts:readAfter/subscribeDurable/log`, `schema/event-log.ts`.

- Follow subscribes to a separate coalesced durable wake channel **before** capturing
  the aggregate watermark. It then reads actual retained rows in ordered pages.
- Exactly one `log.synced` marker is emitted at that captured watermark. `seq` is
  omitted for an empty aggregate. Non-follow reads stop there; follow reads later
  committed rows after wakeups.
- Wakeups carry no event payload. Replay with publish false still wakes log readers,
  and they fetch committed rows instead of trusting a volatile event copy.
- Fork/import reserved sequence ranges do not cause invented events, renumbering,
  reset projections, or a false contiguity requirement. Cursors advance by actual
  stored rows; the synced watermark can cover a reserved range without payload rows.
- The source checkout currently skips unknown durable types in log reads. This port
  intentionally follows the requested stricter contract: unsupported/unknown types
  fail explicitly, without silently dropping them and issuing a successful marker.

`DurableLogItem` is a typed union. Its converter writes either the actual event
envelope or `{ type: "log.synced", aggregateID, seq? }`; it does not wrap events in
another response shape. Server code can use `DurableLogJsonContext` when mounting
the existing experimental Session log route. No endpoint was modified here.

## Replay semantics

SerializedEvent uses the versioned type string, event ID, aggregate ID, sequence,
data and optional created timestamp (source default 0). The payload is decoded and
normalized through the exact implemented definition before applying its projector.

Inside the existing aggregate lock and immediate transaction:

1. A strict nonempty owner mismatch fails first.
2. An older/equal sequence succeeds only when the retained row has the same event ID,
   versioned type, timestamp and deeply equal encoded JSON. This is a no-op: it does
   not project, add usage, notify, or wake. A supplied nonempty owner can fill an
   originally null owner on that exact retry.
3. A new replay from a different nonempty owner is ignored when strictOwner is false.
4. Otherwise sequence must be exactly latest+1, and an event ID already retained at
   any aggregate/sequence fails. No gaps are filled with synthetic events.
5. The canonical projector, sequence/owner update and event row commit together.
   The transaction becomes uninterruptible after aggregate-lock admission, as in
   source replay commit. Projection failures roll back rather than clearing state.
6. A committed replay always wakes durable readers; `publish: true` additionally
   notifies the existing volatile observers. Publish false does not enter that feed.

Object key order is ignored for replay equality; array order is preserved and
numeric comparison follows JavaScript number semantics, including signed zero.

## Ownership and operational boundaries

Claim updates only an existing `event_sequence.owner_id`. It does not create a
missing row, acquire a clustered lease, alter a Session execution claim, schedule
work, publish an event, or reset a resume budget. Local ordinary publication keeps
its existing behavior; replay ownership is separate from execution ownership.

Replay does not invoke local operational commit callbacks. In particular, replayed
execution events project their ordinary read-model effects but do not create/release
`time_suspended` claims or reset resume attempts. Imported transcript rows/counters
are not recreated from Created alone, because source archive import's commit hook
is not serialized. Fork replay requires its actual parent/history/blob dependencies;
missing dependencies fail rather than being guessed or reset.

The explicit registry reuses implemented Session projectors for creation, inbox,
assistant content/steps/tools, usage, instructions, mutation/revert, shell, skill,
movement and fork families. Other Schema-known durable definitions can be decoded
for log reads, but replay without an implemented canonical projector fails. Plugin
projector registration and distributed/cross-process wake transport remain separate
work. The current native store retains event rows; no persist-false mode is invented.

## Repository and verification handoff

The repository's `[Ll]og/` ignore pattern also matches `Core/Event/Log`. The parent
must add a scoped ignore exception for this source directory before staging the
new files. No shared ignore file was changed in this scope.

Validation used only pinned .NET 11 builds with isolated artifacts and
`OpenApiGenerateDocuments=false`. No tests, log subscriptions, claims, replays,
database/SQL operations, SDK construction, processes, models or APIs were executed
as runtime verification.
