# Projected archive import transaction

The concrete adapter is `SessionArchivePersistence : ISessionArchivePersistence`.
Register it using the existing database:

```csharp
services.AddSingleton<ISessionArchivePersistence, SessionArchivePersistence>();
```

Its optional version argument defaults to the actual native
`OpenCodeChannel.ServiceVersion`, not the archive version. An embedding application
with a different app version can supply that value explicitly.

Source: `core/session/transfer.ts` and the existing canonical Created projector.

- Destination resolution uses public ProjectDiscovery. Archive project/location/
  subpath fields do not override the requested destination. The new slug uses the
  source adjective/noun vocabulary.
- Session absence, retained aggregate identity, parent presence and global message
  identities are checked again under the canonical writer transaction. A pending
  inbox identity is not overwritten by an imported visible message either.
- Existing Session IDs conflict, including identical re-imports. Creation uses the
  same SessionCreation.Created definition/projector as ordinary creation, not an
  alternative insert path or fabricated historical event family.
- Settled messages keep IDs, supplied order, payload fields and created timestamps.
  Their projected sequences are 1..N. The aggregate sequence is reserved through
  Created's sequence plus N. These rows are projected archive data, not claimed
  historical durable events.
- Counters, created/updated/idle/viewed/archived times and idle outcome are applied
  in that same commit. Viewed is bounded by idle, and outcome requires idle. The
  adapter does not restore fork/revert state, pending inputs or execution claims.
- Any collision or write failure rolls back Created, message rows, counters and
  sequence reservation together. The stored SessionInfo is returned only after
  EventStore commits. Created observers see the committed imported projection.

Project discovery may persist/announce its own destination project before the
Session transaction, as source does. Import does not synthesize project metadata
from the archive or perform filesystem restore/snapshot reconstruction.

The owned SDK registers the adapter and exposes ExportSessionAsync/ImportSessionAsync.
The Server owner may mount the already implemented import endpoint after registering
this adapter. No Server endpoint or archive preparation/sanitizer file was edited.

Verification is compilation/static checks only. No import/export, SQL/DB runtime,
filesystem/Git/model/tool operations, tests or network calls were executed.
