# Wellknown discovery: host handoff

Owned files are the new `Core/Integrations/Wellknown` subtree,
`Server/Endpoints/WellknownEndpoints.cs`, and `Client/SessionHttpClient.Wellknown.cs`.
No existing endpoint stub, host composition, configuration loader, integration runtime,
plugin runtime, or documentation catalog was edited. The new route is **not mounted yet**.

## Replace the stub once

1. Add `builder.Services.AddWellknownDiscovery()` alongside the existing integration service
   registration. It requires the host's existing `IDatabase` and `CredentialStore`. It must
   use the independently selected dotnet database, not create/open an upstream production DB.
2. In `IntegrationEndpoints.MapIntegrationEndpoints`, replace only the old
   `/api/experimental/integration/wellknown` unavailable stub with
   `app.MapWellknownEndpoints()`. Do not also mount it in `ServerHost`.
3. When a compatible integration contribution reload exists, pass its real delegate:
   `app.MapWellknownEndpoints((location, ct) => ReloadCompatibleIntegrations(location, ct))`.
   This optional callback runs after committed source registration. It must not execute
   `manifest.auth.command`, start auth, download/import a plugin, or synthesize provider/key
   methods. Without it, the endpoint performs real metadata discovery and registry persistence
   only; it does not claim login or a newly runnable integration.
4. Update the separately owned `Documentation/ContractCatalog.cs`: remove
   `v2.experimental.integration.wellknown.add` from the unavailable-operation loop and add
   `Empty("v2.experimental.integration.wellknown.add", typeof(IntegrationWellknownAddPayload))`.
   Leave `v2.plugin.list` to its current owner. The canonical Protocol route already exists.

The one source HTTP operation is:

```text
POST /api/experimental/integration/wellknown
query: existing location[directory], optional location[workspace]
body: { "url": "https://host.example" }
success: 204, no body
discovery failure: 400 InvalidRequestError, kind="well_known_discovery"
```

The handler resolves the normal request Location but never forwards its request headers or
credentials to discovery. Existing host authentication/middleware remains authoritative.
Discovery/persistence errors are sanitized. A reload failure can occur after the origin was
committed; never claim rollback or repeat authentication as recovery.

The existing string Client helper is unchanged. The new overload accepts the already-defined
`IntegrationWellknownAddPayload` plus optional `LocationRef`, uses the normal API client for
the local/server POST, and expects 204. No new list/remove/refresh HTTP endpoints were added.

## Registry and metadata semantics

- The source `core/src/wellknown.ts` requires an `auth` object on add. Its `command` string
  array and `env` string are validated metadata, not an instruction to run a command.
- Discovery GETs `<url-with-trailing-slashes-removed>/.well-known/opencode` anonymously.
  A path prefix is retained as upstream does. URL integration identity is the same saved
  source string, not a guessed provider ID. Query/fragment/userinfo source URLs are rejected.
- `WellknownSourceStore` uses only the existing KV row `wellknown:sources`, with the source
  JSON string-array shape and existing `IDatabase` transaction boundary. Add appends/dedupes
  without reordering existing sources. Remove is a Core domain method only. Unrelated KV rows
  are untouched; no migration or legacy-auth import is introduced.
- Only origins are persisted. Manifest/config caches are in memory and are rediscovered after
  restart. `EntriesAsync` follows saved order and reuses cached entries. `RefreshAsync` fetches
  all saved manifests before replacing the snapshot, then reports semantic changes. Failed
  discovery retains the prior snapshot/registry rather than pruning unreachable sources.
- A malformed source-registry value is reported and left intact instead of silently replacing
  it with an empty list. This is a deliberate preservation boundary beyond upstream's fallback.
- `Updated` is a host-local post-commit/cache-change signal, not a new public durable event.
  Host subscribers should enqueue their existing reload work and unsubscribe on disposal.
  No timer, watcher, second integration runtime, or executor is created here.

## Configuration integration

The config owner can load `WellknownConfigSources.LoadAsync(ct)` and then call
`WellknownConfigSources.Prepend(wellknownSnapshot, localSnapshot)` before the existing
`ConfigSnapshot.Merge()` / `Entries()` operations. This preserves source order:

```text
saved origin 1: inline, remote
saved origin 2: inline, remote
... then all existing global/explicit/project/content sources
```

Use the same host-global `WellknownService`. Loading honors upstream's credential gate:
only the last/selected `CredentialKey` from the **exact URL integration identity** enables
that source's config. No credential means no config contribution. Credential selection uses
the existing `CredentialStore` ordering and Schema credential codec, not a legacy auth file.
Inline config must be an object or null; remote config must be an object, with an object-valued
`config` envelope unwrapped as upstream does. Normalization uses the existing
`ConfigEntryProjection` and canonical `OpenCodeConfiguration` codec. Reload errors become
diagnostics without deleting registered sources.

This is a config-source adapter, not a file editor. It never calls ambient config discovery
or writes `opencode.json/jsonc`; unrelated configured values and comments remain untouched.
The host must install this adapter explicitly; defining it does not claim existing runtime
config consumers have already been wired.

## Credential and runtime boundaries

- `WellknownTransport` owns a dedicated HTTP client with no Console/client pipeline, no
  default/OS/proxy credentials, no cookie jar, and no redirects. Construction does no I/O.
- The discovery GET is always anonymous. Remote config may supply literal metadata headers.
  `{env:...}` substitution accepts only `manifest.auth.env` and the selected key belonging to
  that exact URL integration. It never falls back to `process.env`, Console credentials,
  provider aliases, arbitrary caller variables, or local files.
- Authenticated headers are permitted only for the same scheme/host/port as the saved source.
  Cross-authority authenticated config, credential substitution in URLs, Host/Cookie/proxy-auth
  overrides, and file expansion fail explicitly. Redirect responses are errors. These limits
  intentionally do not reproduce upstream's ambient-env/cross-host expansion behavior.
- Error messages never include remote response bodies, substituted headers, keys, or raw HTTP
  exception text. Configuration documents may contain their own explicitly selected key, as
  upstream config does; do not log those documents or expose them outside existing config policy.
- Downloaded `plugin`/`plugins` declarations remain in the manifest metadata but are excluded
  from executable config-source documents with an unsupported diagnostic. No contribution
  loader is registered until a real compatible runtime boundary is supplied by its owner.
  `auth.command` is never registered as a runnable integration method by this implementation.

## Verification

Build only with repository-local `.dotnet/dotnet.exe`, separate isolated Server and Client
artifact directories, and `-p:OpenApiGenerateDocuments=false`. No tests, server execution,
HTTP/discovery calls, credential/config reads or writes, DB access, native execution, or
auth commands were run for verification. Consult the task report for current build results.
