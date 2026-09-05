# Initial lineage and remaining decisions

This pass adds eleven executable identities: nine February/March transitions and
two credential schema-only transitions. These are conditional source profiles,
not a claim that all historical databases can upgrade.

## Initial/February/March checkpoints

Each row below is an explicit input/output checkpoint requiring the full exact
source journal prefix, not a table count or inferred journal. The first identity
is an input only. Empty databases still use the owner's current-schema bootstrap.

| Identity | Actual transition or input state |
| --- | --- |
| `20260127222353_familiar_lady_ursula` | Completed initial project/message/part/permission/session/todo/session_share schema required |
| `20260211171708_add_project_commands` | ADD nullable project.commands |
| `20260213144116_wakeful_the_professor` | CREATE control_account with source composite PK |
| `20260225215848_workspace` | CREATE legacy workspace with required config, nullable branch and project FK |
| `20260227213759_add_session_workspace_id` | ADD nullable session.workspace_id and source index |
| `20260228203230_blue_harpoon` | CREATE account and account_state, including selected_org_id and source FK |
| `20260303231226_add_workspace_fields` | ADD type/name/directory/extra and DROP config, only for empty workspace |
| `20260309230000_move_org_to_state` | ADD active_org_id, copy selections through active_account_id, DROP selected_org_id, with unmapped-value guard |
| `20260312043431_session_message_cursor` | Replace the two source message/part lookup indexes |
| `20260323234822_events` | CREATE event_sequence and event with aggregate FK; no event rows fabricated |

Profiles distinguish absent/new tables, project.commands, session.workspace_id,
workspace config versus type/name/directory/extra, selected_org_id versus
active_org_id, and original versus cursor lookup indexes. Unrecognized fields,
constraints, journal identities, partial transitions and stale scratch tables
remain rejected. Account/access-token columns are schema definitions only: no
authentication values or production files are loaded by these operations.

## Preserving and blocked data paths

Project commands/session binding are nullable additions; account and event tables
are actual CREATEs. Cursor changes preserve every legacy message/part row and ID.
No event projection, aggregate sequence or event type/version is manufactured.

WorkspaceFields adds required type without a default and discards config. Source
provides neither a type backfill nor a config mapping. This port accepts only an
empty old workspace for that transition. It does not remove populated config or
invent a workspace type. The later name-present rebuild and source NULL-name
constraint failure remain unchanged.

OrganizationState executes the source UPDATE joining account.selected_org_id to
account_state.active_account_id. Before any batch write, it rejects a non-null
selection on an account not referenced by account_state. Such a value would be
discarded by the source DROP COLUMN without being copied. Active selections are
copied exactly; null selections carry no value to lose. Original account IDs,
rows, token fields and other state are untouched. No inactive selection archive
or inferred active account is introduced. Existing active_org_id under an earlier
journal is an unsupported profile, not an overwrite opportunity.

All data guards run before the first requested write. Every actual output profile
is validated before its completion stamp. The requested segment, JSON/backfill
operations and stamps remain one transaction with whole-batch rollback.

## Credential schema-only group

`20260611035744_credential` creates the original credential table: required
connector_id/method_id/label/value, active INTEGER NOT NULL DEFAULT false, source
timestamps and the partial unique connector index WHERE active = 1. It inserts
no values and does not read auth files or other databases.

`20260611192811_lush_chimera` executes the source DROP INDEX, DROP TABLE and CREATE
for the later integration_id/nullable connector/method/active schema **only when
the original credential table is empty**. The guard checks existence of a row,
not its value. Populated id/connector_id/method_id/label/value/active/timestamps
have no source mapping and remain blocked. No CredentialStore code is changed.

The exact CredentialTable checkpoint can be requested to stop before replacement:

```csharp
await SourceMigrationRunner.ApplyAsync(connection, explicitTarget,
    throughMigration: SourceMigrationCatalog.CredentialTable);
```

Both creation and empty replacement are real SQL bodies. Neither implements or
silently completes the later legacy credential import.

## Five non-executable identities: explicit remaining choices

1. **20260127222353_familiar_lady_ursula**: this is initial schema creation, not an
   existing-data upgrade. The migration runner requires it already completed with
   its exact schema. Fresh databases use the separate current source bootstrap.
   Unknown prototypes cannot be relabeled as that initial schema.
2. **20260604172448_event_sourced_session_input**: source deletes input/message/
   event/event_sequence rows, clears session.workspace_id and deletes workspaces
   before rebuilding input/indexes. IDs, prompts, delivery/sequence facts, payloads
   and workspace data would be lost. No truthful preserving map is supplied.
3. **20260622170816_reset_v2_session_state**: source deletes context epochs,
   inputs, messages, events and aggregate sequence rows. No restoration/backfill
   exists in that migration. Loss remains unauthorized.
4. **20260622202450_simplify_session_input**: source deletes those same histories,
   clears non-null session.workspace_id and deletes workspaces. Loss remains
   unauthorized; the runner does not turn it into an empty completion stamp.
5. **20260805200742_import_legacy_credentials**: source reads auth.json and creates
   credential/configuration values. File access and cross-channel import remain
   prohibited. This needs a separate explicit input/import policy, not a skip.

No destructive override has been approved or added. Other conditional blockers
remain: populated permission blobs, pre-order history, session entries, context
epochs and workspace-domain replacement; alternate metadata/name/pre-split
lineages are supported only where separately documented. There is no further
ordinary row-preserving transition among these five identities to invent.

## Validation and ownership

Changes are confined to Database/Migrations. The default-off MigrationTarget hook
is unchanged; no Server registration or shared KV/session/credential/event/input
code is touched. Source SQL is transcribed from the named upstream migration
modules; licensing remains in README.md.

Only isolated pinned .NET 11 Core builds were used. No database opens, SQL or
migration runs, tests, native/app execution, production-data reads, filesystem/
network probes, commits or delegation occurred. Compilation is not proof of
runtime upgrade correctness.
