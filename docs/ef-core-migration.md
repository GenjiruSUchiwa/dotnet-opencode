# EF Core persistence migration

## Status and verification boundary

The Core domain persistence paths now use EF Core 11 SQLite. This is a
whole-persistence source migration, not a second selectable implementation.
Database bootstrap and reviewed incremental migrations remain source-owned.

Verification is **build-only**. No application, SDK host, DI container, DbContext,
EF model, database connection, SQL statement, query-translation probe, migration,
test, or native/provider operation was run to verify this change. Compiling a LINQ
expression does not establish runtime translation or behavioral parity.

The full CLI dependency graph is built with:

```powershell
.\.dotnet\dotnet.exe build src/OpenCode.Cli/OpenCode.Cli.csproj `
  --artifacts-path C:\tmp\opencode\ef-final-20260905-f914 `
  -p:OpenApiGenerateDocuments=false -p:OpenCodePackagePersistentPty=false
```

Build logs are outside the repository at
`C:\tmp\opencode\ef-final-20260905-f914\build.log`. Analyzer completion is tracked
separately in [analyzer-migration.md](./analyzer-migration.md). A successful build
with enabled warnings is not analyzer sign-off. Runtime validation remains
explicitly unauthorized and has not been substituted with model-only probes.

## Package and dependency boundary

- SDK: `11.0.100-preview.7.26381.103` from the repository's `.dotnet` directory.
- `Microsoft.EntityFrameworkCore.Sqlite`: `11.0.0-preview.7.26381.103`, in Core only.
- Existing `Microsoft.Data.Sqlite`: `11.0.0-preview.7.26381.103`.
- Official NuGet nuspecs for both EF SQLite packages identify the `net11.0`
  dependency group and matching minimum versions of SQLite Core, EF Relational,
  and Microsoft.Extensions dependencies. The bundled native SQLite dependency
  remains `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12.
- No Schema-to-EF or Core-to-Server dependency was added. Public models, Vogen
  generation/validation, native ABI, and the TimeProvider and Pipelines work remain
  separate from persistence.

Metadata inspected:

- <https://api.nuget.org/v3-flatcontainer/microsoft.entityframeworkcore.sqlite/11.0.0-preview.7.26381.103/microsoft.entityframeworkcore.sqlite.nuspec>
- <https://api.nuget.org/v3-flatcontainer/microsoft.entityframeworkcore.sqlite.core/11.0.0-preview.7.26381.103/microsoft.entityframeworkcore.sqlite.core.nuspec>

## Pinned-provider source review

The review uses Microsoft's package source commit
[`e2c1e00b3d0f96afb892fb261d5921565b400246`](https://github.com/dotnet/dotnet/tree/e2c1e00b3d0f96afb892fb261d5921565b400246/src/efcore/src),
not assumptions from SQL Server or a different EF release. Paths below are under
that commit's `src/efcore/src/` unless stated otherwise.

| Source | Mechanism reviewed |
| --- | --- |
| EFCore.Sqlite.Core/Storage/Internal/SqliteTypeMappingSource.cs | CLR mapping is selected first; a declared INTEGER store type does not replace the double reader/parameter mapping for event creation times |
| EFCore.Relational/Query/RelationalSqlTranslatingExpressionVisitor.cs | VisitUnary introduces a SQL cast for a C# numeric conversion; nullable wrappers of the same underlying type do not require a cast |
| EFCore.Relational/Query/RelationalMethodCallTranslatorProvider.cs | HasTranslation receives independently mapped operands; custom numeric comparisons therefore retain native INTEGER/REAL comparisons |
| EFCore.Relational/Query/SqlExpressionFactory.cs | Existing operand mappings are retained; comparison results receive the Boolean mapping |
| EFCore.Relational/Query/Internal/Translators/ComparisonTranslator.cs | Two-argument string.Compare translates to SQL comparisons, retaining the stored BINARY collation rather than executing a culture-sensitive CLR comparison |
| EFCore.Relational/Query/RelationalQueryableMethodTranslatingExpressionVisitor.cs | Where/Any, nullable aggregates, ordering, joins, Concat/UNION ALL and collection membership have relational translations |
| EFCore.Relational/Query/RelationalQueryableMethodTranslatingExpressionVisitor.ExecuteUpdate.cs | Single-table property setters, including EF.Property for the selected Session field, remain set-based database mutations |
| EFCore.Sqlite.Core/Query/Internal/SqliteQueryableMethodTranslatingExpressionVisitor.cs | Ordering restrictions do not include the string/long/double columns used here |
| EFCore.Sqlite.Core/Query/Internal/SqliteSqlTranslatingExpressionVisitor.cs | Math.Max translates to SQLite's scalar max; no provider DateTimeOffset/decimal ordering is used |
| EFCore.Relational/Query/Internal/RelationalProjectionBindingExpressionVisitor.cs and EFCore/Query/ReplacingExpressionVisitor.cs | Member-initializer projections and subsequent member access support the explicit SessionDetails read column selection |
| EFCore.Relational/Query/SqlNullabilityProcessor.cs | Conditional JSON-validity guards remain CASE expressions; missing JSON fields are not treated as non-null solely because the document is non-null |
| EFCore.Relational/Query/QuerySqlGenerator.cs and Query/Internal/FromSqlQueryingEnumerable.cs | Uncomposed raw queries execute the supplied statement; typed results use column-name binding. DML RETURNING is enumerated directly, without composing a SELECT around it |
| EFCore.Relational/Storage/RelationalConnection.cs and RelationalTransaction.cs | UseTransaction attaches with transactionOwned=false; disposal does not own the externally opened native connection or transaction |
| EFCore.Relational/Update/Internal/BatchExecutor.cs | SaveChanges may use a savepoint inside the external transaction; it does not commit that transaction |
| EFCore.Relational/Storage/RelationalExecutionStrategyFactory.cs and EFCore.Sqlite.Core/Extensions/SqliteServiceCollectionExtensions.cs | The unconfigured SQLite service graph uses the non-retrying relational execution strategy |
| Microsoft.Data.Sqlite.Core/SqliteTransaction.cs | Non-deferred Serializable transactions issue BEGIN IMMEDIATE; native Commit remains the actual commit operation |
| Microsoft.Data.Sqlite.Core/SqliteValueReader.cs | GetInt32 performs checked narrowing; GetInt64 and GetDouble follow SQLite conversions, while GetValue preserves the storage class |

The matching runtime source
`src/runtime/src/libraries/System.Data.Common/src/System/Data/Common/DbTransaction.cs`
implements the inherited CommitAsync by checking its token and then calling the
synchronous Commit. Passing CancellationToken.None after the existing admission
cancellation check preserves the original commit window; notifications still
follow successful native commit, with the publication gate held.

This is source evidence for the chosen mechanisms, not a claim that a runtime
model or translated query was executed. Provider source copies and source-only
refactoring scratch files live under the isolated artifacts directory, not in the
repository or application package.

### Regressions corrected during review

- Replaced mixed long/double LINQ operators with mapped SQL operators in Session
  pagination, durable replay/log paging, and statistics filters. C# promotion had
  introduced CAST(column AS REAL), losing Int64 distinctions at large boundaries.
- Restored root-independent, single-statement projected-sequence checks. Requiring
  a Session row before reading projections was not equivalent to the original SQL.
- Restored overflow rejection for direct message sequence allocation. Restart
  UPDATE RETURNING now also reads typeof(resume_attempts), preserving the original
  ExecuteScalar `is long` rule instead of accepting an overflowed REAL through a
  typed Int64 reader.
- SessionDetails and credential reads materialize only the columns read by the
  original adapters. Credential active-state validation retains checked Int32
  narrowing before the NULL/zero/one check.
- Message, instruction, credential, project, and log decoding occurs during row
  enumeration rather than after buffering the entire result. This preserves the
  original decode-failure/cancellation order and opaque JSON payload handling.
- Incoming IDs in direct query adapters are extracted before the EF expression,
  avoiding query-parameter evaluation wrapping the existing Vogen validation error.

## Ownership and transactions

`IDatabase` / `SqliteDatabase` still own channel path selection, connection opening,
the private shared-memory anchor, pooling, and connection PRAGMAs. The existing
foreign-key, busy-timeout, WAL, synchronous, cache-size, and checkpoint behavior
has not moved into EF. Data-channel names have not changed.

`PersistenceContext` borrows an already-open connection with
`contextOwnsConnection: false`. It uses `UseTransaction` when the caller owns a
native transaction. Contexts are short-lived operation resources, never singleton
Server/SDK services. Server, SDK, and CLI authentication still compose the database
owner; stores create their own contexts internally.

`EventStore.TransactAsync` preserves this order:

1. Acquire the aggregate publication gate.
2. Begin the native immediate SQLite transaction (`deferred: false`).
3. Attach one EF context to that connection and transaction.
4. Run admission checks, projectors, sequence reservation, and retained-event writes.
5. Commit the outer transaction.
6. Notify durable/live observers while publication serialization remains held.

No SaveChanges interceptor publishes events. There is no new retry strategy around
admission, model calls, tools, or publication. Existing cancellation and replay's
uninterruptible commit window remain in the event owner.

The context defaults to no tracking and relational SQL null semantics. Ordinary
inserts flush immediately and detach their rows so subsequent LINQ or intrinsic
operations see the write without a stale change-tracker copy. SQLite exceptions
wrapped by SaveChanges are rethrown as their original provider exception. Set-based
writes retain explicit row-count checks where the domain requires them.

There is no universal timestamp interceptor. Producer-specific timestamps still
come from the existing injected clocks. Operational updates that must not count
as Session activity do not set `time_updated`.

## Schema mapping

`Persistence/Rows.cs` contains storage-only scalar rows. JSON remains opaque text;
EF inheritance and owned JSON mappings are not used. This avoids dropping unknown
payload properties or applying public DTO defaults during a database read/write.

IDs are stored as strings in these internal rows. Existing domain adapters call
the same `FromExisting` factories when producing public values. No EF converter is
needed for a scalar string row, and no unused Vogen converter layer is introduced.
In particular, legacy `ses` IDs, non-prefixed project identities such as `global`,
and message-ID validation are not tightened. Public ID generation is unchanged.

The model maps these 19 tables:

| Table | Key | JSON TEXT columns |
| --- | --- | --- |
| account_state | id, INTEGER | — |
| account | id, TEXT | — |
| control_account | email + url, TEXT | — |
| credential | id, TEXT | value |
| event_sequence | aggregate_id, TEXT | — |
| event | id, TEXT | data |
| kv | key, TEXT | value for the current consumers |
| permission | id, TEXT | — |
| project_directory | project_id + directory, TEXT | — |
| project | id, TEXT | sandboxes, commands |
| instruction_blob | hash, TEXT | value |
| instruction_entry | session_id + key, TEXT | value |
| instruction_state | session_id, TEXT | initial_values, current_values |
| session_inbox | id, TEXT | payload |
| session_message | id, TEXT | data |
| session_pending | id, TEXT | data |
| session_v2 | id, TEXT | fork_boundary, summary_diffs, metadata, revert, permission, model |
| workspace | id, TEXT | binding |
| worktree | project_id + directory, TEXT | — |

`migration(id TEXT PRIMARY KEY, time_completed INTEGER NOT NULL)` is deliberately
not an EF entity. It belongs exclusively to the source migration runner.

The 16 explicit index names and ordered columns are:

| Name | Columns | Constraint |
| --- | --- | --- |
| event_aggregate_seq_idx | aggregate_id, seq | unique |
| event_aggregate_type_seq_idx | aggregate_id, type, seq | |
| permission_project_action_resource_idx | project_id, action, resource | unique |
| session_inbox_session_delivery_seq_idx | session_id, delivery, enqueued_seq | |
| session_inbox_session_enqueued_seq_idx | session_id, enqueued_seq | unique |
| session_message_session_seq_idx | session_id, seq | unique |
| session_message_session_type_seq_idx | session_id, type, seq | |
| session_message_session_time_created_id_idx | session_id, time_created, id | |
| session_message_time_created_idx | time_created | |
| session_pending_session_delivery_seq_idx | session_id, delivery, admitted_seq | |
| session_pending_session_compaction_idx | session_id | unique; type = 'compaction' |
| session_pending_session_admitted_seq_idx | session_id, admitted_seq | unique |
| session_v2_project_idx | project_id | |
| session_v2_workspace_idx | workspace_id | |
| session_v2_parent_idx | parent_id | |
| session_v2_time_suspended_idx | time_suspended | time_suspended is not null |

The foreign-key-index convention is removed. Only source-declared relationships
are mapped: account-state to account uses SET NULL; event to event-sequence and
the existing project/session dependents use CASCADE. Parent, fork, and workspace
identities on Session are not invented foreign keys. No auto-generated identity
values or new indexes are introduced.

Epoch fields retain INTEGER affinity and milliseconds; cost retains REAL affinity.
Event creation time remains a double even with INTEGER affinity. Projection read
models retain their previous integral reads; intrinsics preserve fractional replay
creation times and numeric usage writes without rounding them through those read
models. Credential active state remains a nullable integer and the decoder still
rejects values other than NULL, zero, and one.

EF requires non-null tracked keys. The generated source DDL's historical omission
of explicit NOT NULL on single TEXT primary keys is not rewritten: EF is never
used to create or migrate this schema. `DatabaseBootstrapSchema.Generated.cs` and
`SourceSchema` remain the authoritative DDL/profile definitions.

## Source migration authority

There are no calls to EF EnsureCreated, EnsureDeleted, or Migrate, no EF migration
assembly, and no EF history table. The current 46-entry baseline still ends at
`20260823191254_nullable_workspace_binding`.

`DatabaseBootstrap.Apply` retains fresh-schema stamping and the strict default
current-baseline gate. `SourceMigrationRunner` retains the explicit owner-selected
`MigrationTarget`, canonical/named-Drizzle/timestamp-Drizzle journal classification,
exact-prefix validation, pre-split marker, blocked destructive/legacy transitions,
schema-profile checks, and whole-batch rollback. Foreign keys are toggled before
the native migration transaction, checked before commit, and restored afterward.

No current baseline was stamped on an existing database as part of this change.
There is no parallel writable migration history.

## Domain coverage

| Area | Converted source |
| --- | --- |
| Sessions and messages | Database/SessionStore; Session/SessionQueries |
| Event append, claim, replay, log paging | Event/EventStore; Event/Log/DurableEventLog |
| Admission and consumption | Event/SessionAdmission; SessionInboxOperations; CompactionInbox; Session/Transfer/MoveInbox |
| Assistant and execution projection | AssistantProjector; ExecutionProjector; RestartPersistence |
| Instructions | InstructionPersistence; InstructionEntryPersistence; Session/ReadInstructionLoader |
| Background KV | JobBackgroundStore |
| Session mutations and revert | SessionMutationProjector; SessionRevertPersistence |
| Creation, shell, title, skills, synthetic messages | SessionCreation; SessionShellPersistence; SessionTitlePersistence; SessionSkillPublisher; SessionSyntheticProjector |
| Compaction and usage | CompactionProjector |
| Fork, movement, archive import | Session/Transfer/SessionTransfer; ForkProjector; MoveProjector; Event/SessionArchivePersistence |
| Projects and worktrees | Projects/ProjectQueries; ProjectMutations; ProjectDiscovery; Locations/CatalogLocation; Worktrees/WorktreeStore |
| Credentials and saved permissions | Database/CredentialStore; Permissions/SqlitePermissionGrantStore |
| Other KV consumers | Integrations/Wellknown/WellknownSourceStore; WebSearch/WebSearchSelectionStore |
| Statistics | Session/Statistics/SessionStatistics |

The old domain SqliteCommand factory and SqliteDataReader materializers are removed.
Remaining direct ADO use is limited to connection initialization and source schema
migration/classification. No duplicated writable adapter remains.

## Complete retained SQL inventory

All runtime SQL below is parameterized through EF APIs. `SqliteIntrinsics` owns
the named mutations, and `StatisticsSql` owns the two JSON-expanded statistics
queries. These are supported boundaries, not temporary legacy adapters.

| Intrinsic | Reason |
| --- | --- |
| ReserveSequenceAsync | max-based reservation; preserve owner and a projector's higher reservation |
| ReserveReplayAsync | max reservation plus conditional owner adoption |
| PutKvAsync | upsert preserving creation time |
| PutProjectAsync | null-safe conditional VCS upsert; unchanged discovery must not touch time/path/display |
| PutWorktreeAsync | null-safe conditional strategy upsert; affected-row count is the changed result |
| AddWorktreeAsync | insert-on-conflict-no-op producer record |
| AddPermissionAsync | insert-on-conflict-no-op across both ID and semantic unique key |
| PutInstructionAsync | conditional upsert with SQL NULL/value distinction and removed-state reset |
| PutInstructionBlobAsync | content-addressed first insertion wins |
| PutInstructionStateAsync | update current/through values without resetting an established epoch |
| InsertSessionAsync | conflict result and unrounded replay creation time |
| InsertInboxAsync | conflict result and unrounded enqueue time |
| InsertMessageAsync | shared event projection insert preserving double creation time |
| TouchSessionAsync | unrounded event time in INTEGER-affinity activity column |
| SaveAssistantAsync | opaque JSON and double payload creation time; assistant ownership predicate |
| SaveCompactionAsync | opaque JSON and double payload creation time |
| AddUsageAsync | source SQL numeric arithmetic, including INTEGER-affinity counters |
| ConsumeInboxAsync | DELETE RETURNING supplies the exact consumed payload before projection |
| ClaimExecutionAsync | conditional local claim with unrounded event time |
| ClearCurrentRetryAsync | JSON key removal on only the newest incomplete assistant |
| CompleteExecutionAsync | monotonic idle time and replay-sensitive operational-claim preservation |
| IncrementResumeAsync | UPDATE RETURNING count drives the same transaction's next durable event; typeof preserves integer-only result acceptance |
| RestoreArchiveAsync | exact numeric usage/time restoration and existing outcome serialization |
| ForkSessionAsync | INSERT SELECT preserves parent fields and source conflict behavior |
| ForkMessagesAsync | INSERT SELECT preserves sequence gaps, timestamps, JSON, and settled filters |
| ForkInstructionEntryAsync | preserve double event time and SQL NULL versus JSON text |
| ForkInstructionStateAsync | inherited initial/current values, insert-on-conflict-no-op |
| HighestProjectionAsync | one-snapshot maximum over messages/inbox, independent of Session-row presence |
| HasUnsequencedProjectionAsync | one-snapshot comparison with the aggregate watermark, without a Session-table root |
| StatisticsSql.SummaryAsync | MATERIALIZED CTE, json_each, aggregate FILTER and SQL null behavior |
| StatisticsSql.DetailAsync | json_each plus source duration CASE/coalesce behavior |

Source migration SQL and bootstrap PRAGMAs are additionally retained unchanged in
their existing database-owner files. SQL scalar JSON functions used by LINQ are
explicitly mapped (`json_extract`, `json_valid`); there is no assumption
that SQL Server JSON features apply to SQLite.

`SqliteFunctions.After`, `Before`, `AtOrAfter`, and `At` are SQL-expression
translations, not SQLite user-defined functions. Their bodies are never intended
for client evaluation. They preserve the independently mapped INTEGER column and
REAL bound instead of introducing an implicit C# numeric promotion.

## Owned analyzer cleanup

No package-wide NoWarn or new global analyzer exemption was added.

- Implicit continuation capture is now explicit ConfigureAwait(true) in the
  owned Core files, including resource disposal and async enumeration. Existing
  ConfigureAwait(false) calls remain false. Resource acquisition, disposal scope,
  native immediate transactions, and the commit/notify boundary retain their order.
- SDK drain retirement and the server boot Task.Run explicitly use
  CancellationToken.None where the source previously used the default token.
  Passing an already-cancelled host/request token would change lifecycle behavior.
- Windows storage-prefix recognition now uses bounded ASCII/character checks.
  Remaining owned regexes use NonBacktracking and named captures where applicable;
  their patterns use no backreferences/lookarounds or repeated capture histories.
- Native enum-zero comparisons use FileAttributes.None / UnixFileMode.None, not
  a different flag. Previously implicit culture-sensitive parsing/comparison uses
  CurrentCulture explicitly; it was not silently changed to Invariant or Ordinal.
- Source migrations retain their exact SQL/journal authority. Their changes here
  are explicit disposal continuation, equivalent schema-text matching, and the
  narrowly scoped restoration-failure exception below.

The following method-level exceptions are explicit compatibility decisions:

| Rule | Scope | Reason |
| --- | --- | --- |
| MA0015 | DurableLogItemJsonConverter.Write | Preserve the existing inferred aggregate-field parameter name |
| MA0015 | ReadInstructionLoader.LoadAsync | Compound paths/project-root validation retains its original message |
| MA0015 | SessionStatistics.GetAsync, Time, Zone | API-visible validation text, field names, and causes remain unchanged |
| MA0015 | WebSearchSelectionStore.SaveAsync | Preserve provider-selection error text |
| MA0015 | AuthCommands.RunAsync, ChooseAsync, OpenBrowser | Preserve login validation errors; noninteractive process state must not be relabelled as an invalid title argument |
| MA0015 | ServerHost.CreateAppCore, ParsePort | Preserve startup diagnostic messages and selected-port field identity |
| MA0072 | SourceMigrationRunner.ApplyAsync | Restoration failure must remain observable, with original and restoration failures retained together; a committed batch must not be presented as safe to retry |
| MA0042 | ServiceLifetime.StoppingAsync | Preserve synchronous monitor cancellation before returning the completed lifecycle task |

Whole-graph diagnostics can include other workers' disjoint modernization work.
The final build report distinguishes those from diagnostics in this owned scope;
compilation is not runtime or schema-parity sign-off.

## Compatibility details and remaining limitations

- LIKE retains `%` and `_` wildcard behavior; it is not replaced by Contains.
- Keyset tie ordering and previous-page reversal remain explicit. Normal limits
  translate to Take; negative SQLite limits remain unbounded. Limits above Int32
  are streamed without narrowing the public Int64 limit.
- Credential ordering retains NULL/false/true distinctions and binary identity
  matching. Row decoders retain existing JSON validation and errors.
- Replay uses the existing structural JSON equality implementation, including
  signed zero, rather than text equality or EF tracking.
- Archive import retains only settled projected history. It does not fabricate
  historical events. Project resolution remains outside the Session commit.
- Forking retains the selected high-water mark even when that boundary row is
  unsettled. Copied payload references are not rewritten.
- Statistics retains 31-day windows, half-open bounds, fork exclusions, local
  timezone/median work, and the existing unversioned compaction-event type query.
- This migration deliberately does not fix the existing archive-outcome
  serialization/read discrepancy. Such a behavior change needs its own review.
- No runtime query translation, schema materialization, concurrency, rollback,
  performance, or migration parity has been tested. Those are not implied by the
  build result and must remain a separate explicitly authorized verification step.

## Follow-up after the accepted EF checkpoint

[Persistence parity follow-up](./persistence-parity-pass.md) records the subsequent
Forms/Reference diagnostic fixes and actual source gaps fixed in Session path
codecs, direct Core query defaults, previous-page decode order, move history, and
fork parent/path handling. It also records the missing `session.usage.updated`
Schema/event-contract counterpart without changing runtime foundations or Schema.
These changes do not alter migration authority or the documented archive/statistics
quirks. ServerHost is owned by the network worker and was not edited in that pass.
