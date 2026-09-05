# Registry API and Source Mapping

`ToolRegistry` is a Location-owned replayable transform service. It installs no
tools, has no permission-assertion callback, and does not enable the runner. The
existing `ToolRegistry(HttpClient)` call still compiles; HttpClient no longer causes
builtin installation. The host must move registry ownership into its Location graph
before enabling tools; an existing process-global DI registration is not that graph.

## Consumer Migration

The old executable `ITool` interface and dictionary APIs (`Register`, `Get`, `GetAll`,
registry `ExecuteAsync`, and provider-specific definition builders) were removed.
There is one executable record: `ToolInfo`, containing name, description, input
codec, executor, optional output codec and options. Builtin classes are producers;
their `Create()` methods return this same record. No second executable registry
entry or compiled executor wrapper is stored.

Register complete definitions through synchronous transforms, retaining the returned
`ToolRegistration` in the producer's scope:

```csharp
var read = new ReadTool(filePolicy,
    new ReadInstructionDiscovery(location, loadReadInstructions)).Create();
using var registration = registry.Transform(draft => draft.Add(read));

var snapshot = registry.Snapshot(agentPermissions);
// Advertise snapshot.Definitions for this request, then retain this snapshot.
var result = await snapshot.ExecuteAsync(name, input, context, cancellationToken);
```

Registration supports `IDisposable` and `IAsyncDisposable`; disposal is idempotent.
Both remove only the owning transform and replay the remaining transforms. Registry
disposal is terminal teardown without rebuilding. It does not revoke already
captured snapshots or dispose producer dependencies on their behalf.

Use `draft.List()` or `draft.Get(effectiveId)` inside transforms for inspection.
Use `draft.Update(id, tool => tool with { Description = "..." })` for changes, and
`draft.Remove(id)` for removal. Updates/removals of missing names do nothing.
Update restores the original name and namespace, even if the callback changes them.
Invalid additions/updates are skipped and reported through `RegistrationErrors`;
invalid updates leave the prior effective definition intact. Invalid codec schemas
can fail earlier, when the producer constructs its codec.

Use `await registry.ReloadAsync(ct)` when captured producer source data changes.
Transforms replay in their original registration order; reload does not move a
producer to the end. This is the seam for a stable MCP transform reading updated
discovery data. Reload uses a trailing-edge 500 ms debounce. Cancelling one waiter
does not cancel shared rebuilding. `registry.Batch(action)` coalesces synchronous
registration/disposal changes for this registry into one rebuild.

## Ordered State

The implementation maps `core/src/tool.ts` and `core/src/state.ts` as follows:

- Fresh draft on each rebuild; active synchronous transforms replay in order.
- Latest valid add for an effective name wins. Removal and updates participate in
  replay just like adds. Disposing an override can reveal an earlier registration.
- Names replace each non-ASCII-alphanumeric/underscore/hyphen UTF-16 character with
  `_`, then require 1 to 64 characters. Namespace segments use the same character
  set and size restriction without replacement. Dots flatten to underscores before
  prefixing the tool name.
- Native `execute` is reserved for CodeMode. Namespace-qualified names can differ
  from that reserved effective name. Pinned options require CodeMode eligibility.
- Name matching is ordinal/case-sensitive. Definitions are sorted ordinally by
  effective name for native advertising.
- State commits only after all transforms finish. Callback defects propagate; a
  newly added transform that throws is rolled back so no unreachable disposable is
  leaked. A failed rebuild clears the current catalog and new snapshots throw
  `ToolCatalogUnavailableException` until a rebuild succeeds. In particular, a
  disposed registration cannot survive in new snapshots when another transform
  throws. Previously captured snapshots remain valid. This is a fail-closed .NET
  ownership adaptation to source Scope finalization.
- Drafts expire after replay. Transforms must not call registry mutation recursively.

`Batch` is registry-local. Source cross-State ambient batching/inheritance is not
implemented. Terminal registry disposal corresponds to teardown without flush.

## Snapshots and Execution

A `ToolSnapshot` captures immutable `ToolInfo` records, options, and cloned advertised
schemas. Later registrations, updates, reloads or disposal cannot replace the
executor selected by that snapshot. Executors/codecs still retain references to
mutable producer state; this matches the source's shallow registration capture.
Producer codec schemas must be immutable after construction.

Optional permission rules perform catalog filtering only: the last action-matching
rule wholly disables a tool only when its resource is `*` and effect is deny. Tools
use `Options.Permission` or their effective name for that filter. Execution still
uses leaf authorization, never a registry permission callback.

`Options.CodeMode` defaults to true as in the source. Such entries appear in
`CodeModeCatalog`, not native `Definitions`. The CodeMode runtime itself is absent;
no fake `execute` tool is advertised and catalog entries cannot be invoked through
native `ExecuteAsync`. All current builtin producers explicitly set CodeMode false.
Porting CodeMode is still required for source-complete registration behavior.

Optional `IToolExecutionHooks` implements before-repair and completed/error hooks.
An optional definitions dictionary on `ExecuteAsync` enforces surviving native
definitions and alias resolution after the before hook. Input decoding precedes
execution; declared output encoding follows it. Missing declared output is a
`ToolExecutionException`; output without a schema is a `ToolContractException`.
Only explicitly declared `ToolExecutionException` failures reach the recoverable
after-error hook. Undeclared executor exceptions, programming defects, permission
blocked/declined control flow and interruption propagate unchanged; there is no
exception-name exclusion list. Input/output codec `ToolValidationException` failures
are explicitly converted at their codec boundaries. Leaf input/semantic failures
use `ToolExecutionException`; other environment/service failures are not implicitly
made recoverable by the registry.

`ToolExecutionResult.Content` is now a list of canonical Schema `ToolContent` parts,
not a string. The string constructors remain convenient for leaves. `HasOutput`
distinguishes absence from explicit JSON null; assigning `Output`, including null,
marks it present. Snapshot execution returns encoded JSON output and normalized
content. Empty/missing content falls back to output text. Success hooks run after
schema encoding, matching the source; their transformed output is not re-encoded.
Image normalization and the full PluginHooks runtime remain unimplemented.

## Codec Contract

`IToolValueCodec` advertises JSON Schema and exposes asynchronous `DecodeAsync`
(JSON to native value) and `EncodeAsync` (native value to JSON). Custom codecs must
perform their actual validation and throw `ToolValidationException` for validation
issues. They may support richer native types/schema features; there is no default
always-valid implementation. Type erasure ends producer type safety at `ToolInfo`.

`ToolInfo.FromJson` uses the concrete `JsonToolCodec`. Its explicit supported subset:

- Boolean schemas, `type` (including type arrays), `enum`, and `const`.
- `properties`, `required`, schema/boolean `additionalProperties`, and homogeneous
  array `items`.
- `allOf`, `anyOf`, `oneOf`, and `not`.
- `minimum`, `maximum`, numeric `exclusiveMinimum`/`exclusiveMaximum`.
- `minLength`/`maxLength` using Unicode scalar counts, `minItems`/`maxItems`, and
  `minProperties`/`maxProperties`.
- Annotations `title`, `description`, `$comment`, `default`, `examples`, and the
  documented draft-07/2020-12 `$schema` identifiers. Defaults are not injected.

Unsupported keywords, including `$ref`, `$defs`, `pattern`, `format`, tuple items,
conditionals and unevaluated-property rules, fail construction explicitly. Schema
depth is capped at 64. Numeric assertions use exact .NET decimal values and reject
unrepresentable precision/range rather than round a constraint. Duplicate instance
properties are rejected when object schemas are evaluated. Input values otherwise
remain intact; object extras are not silently stripped.

Both input and output are validated against the supported schema, stronger than
the upstream raw-JSON output branch which only checks JSON compatibility. Encoded
native objects use camel-case JSON naming and their explicit serialization attributes.
Read/edit/write/search/skill/shell/webfetch producers declare actual structured output schemas.
Webfetch is opt-in through a dedicated anonymous transport; see `WEBFETCH.md`.

`ToolLocationFactory` provides concrete opt-in composition for the five supported
local filesystem builtins plus skill loading. See `HOST-COMPOSITION.md`. It uses the same registry
and authoritative permission map without enabling model advertising.

## Verification

Build-only verification uses isolated artifacts outside the repository. No tests,
tool invocations, applications, child-process adapters, provider calls or database
operations were run. Keep auto-registration and runner execution disabled until
the complete host permission/Location pipeline is wired and verified.
