# Application Build Identity

`OpenCodeChannel.ServiceVersion` is a semantic protocol/application version, not
a source revision. It must not be the sole criterion for managed daemon reuse.

`Directory.Build.targets` runs the MSBuild-only `ComputeApplicationBuild` task
for Protocol. It hashes sorted relative source/asset paths and file bytes under
`src`, plus the root SDK/build/package configuration and fingerprint task itself.
The build flavor includes Configuration, TargetFramework, RuntimeIdentifier and
NETCoreSdkVersion. Generated bin/obj trees, the local SDK, tests and live runtime
state are excluded. No process execution, git command, application load, or
database access occurs in this task.

The task emits `ApplicationBuild.Id` as a compile-time constant in
OpenCode.Protocol and an `opencode-build.id` output sidecar. The constant is
inlined into Client and Server consumers, so replacing only a sidecar or a shared
DLL cannot make an older Server assembly report new source identity. The sidecar
flows through normal project-reference output/publish copying.

The identity contains no build timestamp, absolute checkout path, or temporary
output directory. Identical inputs/flavor therefore have identical identity,
even when run.ps1 uses a new artifacts directory. The full deployment file hash
still protects the immutable runtime snapshot separately; PDB/output-path
differences do not force daemon replacement when source identity is unchanged.

`run.ps1` obtains an expected fingerprint through the explicit MSBuild target
before compilation, supplies it to the build, recomputes it afterward, and checks
CLI/Server sidecars before packaging or launch. Edits during this window fail
rather than silently mixing versions. Ordinary stable-source builds also emit
identity, but callers needing the before/after coherence guard should use run.ps1.

## Handshake And Replacement

Authenticated health and the private registration include `buildID`. Managed
Client discovery compares it with the compiled Client fingerprint (or a supplied
ExpectedBuildId), in addition to version and instance identity. Missing buildID
from an older daemon is incompatible, not a successful stale connection.

For an implicit managed server, Ensure permits one replacement when enabled:

1. Verify the registered loopback instance's password, application, channel, ID,
   PID and version through health. Registration and health buildID must agree
   when registration supplies it.
2. Confirm the selected replacement package carries the expected build stamp.
3. Require a successful canonical active-session response with an empty map.
4. Recheck the exact registration and POST its authenticated instance-bound
   `/api/service/stop`; never signal a PID or delete registration from the client.
5. Wait for registration/lease release, then rediscover or start the immutable
   replacement and require the expected build in its health response.

The idle check is an observation, not a global transaction with concurrent
admissions. Cooperative host shutdown semantics still apply; this is not an
exactly-once or uninterrupted-session guarantee. Align simultaneous development
clients to the same source build. Automatic replacement is bounded to one attempt
per Ensure and never force-terminates an unresponsive process.

Busy, unknown, unauthenticated, unsupported-active-list, or explicit-server cases
return a typed mismatch/error with an action. For a verified managed .NET
instance, finish work and use `./run.ps1 stop`, then `./run.ps1`, when an explicit
transition is needed. No old or unrelated OpenCode server is controlled through
this feature. ReplaceIncompatible=false disables automatic replacement.

An explicit server is never replaced and defaults to its version policy; an
explicit ExpectedBuildId can additionally constrain it. Custom daemon commands
must point to a complete stamped package. The client never treats a missing or
mismatched package stamp as permission to stop a working incumbent.

Verification for this work is build/source inspection only. A screenshot of an
obsolete message is consistent with stale daemon reuse but does not prove which
running process produced it; no live registration, health probe, or stop operation
was used during implementation.
