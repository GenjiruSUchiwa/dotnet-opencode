# Timestamped local service negotiation

`run.ps1` builds the **`dotnet-local`** channel. Published NuGet builds remain on
**`dotnet`**. The two channels do not share registration, database, deployment, or
client-state files. Existing `dotnet` data is not renamed, imported, or deleted.

| Identity | Source launcher | Published tool |
| --- | --- | --- |
| Channel | `dotnet-local` | `dotnet` |
| Registration/config filename | `service-dotnet-local.json` | `service-dotnet.json` |
| Database filename | `opencode-dotnet-local.db` | `opencode-dotnet.db` |
| Runtime deployment directory | `service-dotnet-local/deployments` | `service-dotnet/deployments` |
| Service version | `0.1.0-dotnet-local.<UTC timestamp>` | Existing protocol service version |

Both retain the default port 5055. To run both channels simultaneously, configure
a different port for one, for example with `OPENCODE_DOTNET_PORT`. An occupied port
is not permission to stop the other channel. Provider User-Agent stays
`dotnet-opencode` for both.

## Stable incremental builds

- The source fingerprint includes the local-build mode but not wall-clock time.
- `run.ps1` stores the successful fingerprint and UTC Unix-millisecond timestamp
  in the ignored build cache's `local-build.json`.
- An unchanged fingerprint reuses that timestamp. A changed fingerprint gets a
  new timestamp, at least one millisecond beyond the cache's preceding timestamp.
- The Protocol assembly embeds `ApplicationBuild.Id`, `.Timestamp`, and `.Version`.
  CLI and Server must carry identical `opencode-build.id` and
  `opencode-build.timestamp` files before the launcher copies a runnable payload.
- Fingerprint-only MSBuild calls do not rewrite generated version code. An unchanged
  source launch therefore remains an incremental build, not a timestamp-triggered rebuild.

## Negotiation and replacement

Local startup requires an exact channel, application identity, build fingerprint,
timestamp and service-version match. Custom version predicates or explicit server
selection cannot opt a local UI out of its exact build/timestamp checks.

1. Reuse a ready server only when it matches the local client.
2. For an authenticated, registered **older** local server, verify the replacement
   package first, preserve any supported persistent-PTY handoff, request instance-bound
   shutdown, wait for registration/election release, and launch the matching server.
3. Local replacement does not require idle Sessions. Normal host shutdown preserves
   the existing durable execution-recovery policy. Active work may be interrupted.
4. Never downgrade a newer local server or replace a conflicting equal timestamp.
5. Unknown identity, invalid health, unsupported handoff, failed shutdown, or a
   startup failure aborts connection. There is no fallback to a mismatched server.

Shutdown is cooperative, with up to 30 seconds for a local upgrade. This does not
add PID/name-based forced termination. Published `dotnet` builds retain their
existing idle-only replacement policy. Explicit servers are never replaced.
The persistent server remains detached from the TUI/desktop lifetime.

This policy is implemented in source. Build/static verification does not establish
live upgrade, cancellation, recovery, or process-lifetime behavior.

## Build verification

- Both final local and published-channel CLI dependency builds passed with zero
  warnings and zero errors using SDK `11.0.100-preview.7.26381.103` and
  `OpenApiGenerateDocuments=false`.
- The repeated unchanged local build took 10.41 seconds, skipped all 11 CoreCompile
  targets, and retained the CLI DLL timestamp after fingerprint-only checks.
- CLI output, Server output and staged `server/` all carried the same local timestamp.
  The published-channel build carried timestamp zero instead.
- Evidence logs: `C:/tmp/opencode/dotnet-local-final-build.log`,
  `dotnet-local-second-build.log`, and `published-channel-final-build.log`.
- No tests, application startup, database operations, or live replacement were
  executed for this verification.
