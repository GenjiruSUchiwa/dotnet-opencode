# Database Bootstrap

The default file is `opencode-dotnet.db` in the existing OpenCode data directory.
No production database or credentials are copied during initialization.

## Source

`DatabaseBootstrapSchema.Generated.cs` contains the complete upstream generated
schema: 19 tables and 16 explicit indexes, plus the ordered 46-entry migration
registry. SQLite also creates indexes implied by primary-key constraints.
The generated file retains the upstream MIT license, source commit, and SHA-256
hashes of the input schema, registry, migration runner, and schema generator.

Regenerate from a reviewed upstream checkout with:

```powershell
./GenerateBootstrap.ps1 -UpstreamRoot <upstream-checkout>
```

The script reads source files and generates C# literals. It does not execute
TypeScript, SQL, SQLite, tests, or application code. It rejects unrecognized
schema-generator syntax rather than silently extracting a partial snapshot.
Revisit `DatabaseBootstrap.cs` whenever the upstream migration runner changes.

## Fresh Databases

The classification matches `packages/core/src/database/migration.ts`: ignore
SQLite internal tables and underscore-prefixed embedder tables. If no other
tables exist, create the full schema, create `migration(id, time_completed)`,
and record every baseline ID in one transaction. An immediate transaction
serializes classification and bootstrap; failure rolls back the whole schema
and its history. An empty file may remain after a failed bootstrap and can be
retried. Existing underscore-prefixed embedder tables are not altered.

Baseline history is intentional upstream behavior, not a claim that legacy data
transformations ran. There is no existing OpenCode session data to transform.
The upstream fresh path also does not execute legacy credential import or any
other incremental migration body. This implementation preserves that behavior.

## Existing Databases

A database containing `session` or `session_v2` never enters fresh bootstrap.
It must already contain every canonical table/index name and the exact baseline
migration-ID set. Otherwise it is rejected without repairing its schema or
adding migration records. Other nonempty application databases are rejected.

This conservative guard is not full schema-integrity validation. The legacy
Drizzle journal reconciliation, incremental SQL/data migrations, credential
import, and migration-specific foreign-key handling remain unimplemented.
Older, partial, and newer migration histories are therefore unsupported. Do not
manually stamp a journal or copy a live database to bypass this boundary.

`SqliteDatabase.IsInitialized` becomes true only after successful initialization
and connection setup. It does not imply session-runner or provider readiness.
