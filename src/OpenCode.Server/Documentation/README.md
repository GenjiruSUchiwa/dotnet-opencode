# Registered OpenAPI surface

`MapNativeOpenApi` authors authenticated `GET /openapi.json`. The route uses the
same readiness, credential/query-token, and CORS middleware as other ordinary API
routes. It is not invoked during builds or verification.

The builder reads the pinned `Protocol/Documentation/canonical-openapi.json`
reference, checks its embedded SHA-256, and joins canonical method/path shapes to
the actual `EndpointDataSource`. Parameter names may differ internally without
changing URL matching. Only uniquely matched, verified contracts appear in paths.
The source document never registers a route and cannot make a missing route appear
implemented. Duplicate routes, missing native metadata, and schema export failures
are reported in `x-opencode-documentation.unmappedContracts`.

`ContractCatalog` contains documentation-only bindings verified against actual
handlers. It does not edit Shell/Skill endpoint ownership or run delegates.
Always-unavailable handlers explicitly document 503, not fictional success.
Native extensions without verified contracts and source operations without native
routes are reported separately. Counts describe metadata coverage, not feature
parity or runtime/provider availability.

## Schemas and wire semantics

Native response schemas use .NET `JsonSchemaExporter` and canonical Schema/Protocol
types. Request bodies retain the canonical source contract and also expose
`x-native-request-schema` for the actual native codec shape. Named canonical models
retain source stable IDs; native wrappers have deterministic Native-prefixed IDs.

Custom codec mappings cover ID strings (including actual legacy prefix behavior),
finite versus named non-finite numbers, positive/nonnegative integers, epoch time
range/truncation, omit-only optionals, nonempty arrays, dictionaries, Form values,
MCP configuration/OAuth, token usage, inbox/message unions, and configuration unions.
Polymorphic tags and mappings come from real type metadata or the actual custom
message/inbox codecs. Unknown typed codecs fail export; they are not replaced with
an empty object. Unconstrained JSON metadata is explicitly a JSON-value schema.

Config codecs reject explicit null but optional constructor defaults use null as
an omission sentinel. Where the framework tries to serialize that invalid default,
the exporter reads the same JsonTypeInfo properties directly instead of weakening
the real converter. Nullable response data, such as VCS base, remains required and
can be null. Body schemas do not pretend to encode every domain validation branch;
native limitations and diagnostics remain explicit.

Binary file bytes, SSE, and WebSocket upgrade responses are not described as JSON
objects. Basic auth, auth_token precedence, and one-use PTY tickets are documented
without embedding credentials. Local JSON Pointer and discriminator references are
checked before an operation is included, and unused source schemas are pruned.

## Metadata-only tools

Import a reviewed source snapshot explicitly:

```powershell
.\.dotnet\dotnet.exe msbuild src/OpenCode.Protocol/Documentation/ImportCanonicalOpenApi.proj `
  -target:ImportCanonicalOpenApi -property:Source=<typescript-checkout>/packages/protocol/openapi.json
```

Generate Schema/Protocol metadata without an application or Server/Core reference:

```powershell
.\.dotnet\dotnet.exe msbuild build/ExportOpenApiSchemas.proj -target:ExportOpenApiSchemas `
  -property:ArtifactsPath=C:/tmp/opencode/openapi-metadata
```

This explicit .NET build task reuses the exporter source and writes schema roots,
unmapped-codec diagnostics, and unresolved-reference diagnostics. It does not
instantiate WebApplication, discover routes by executing a host, call handlers,
open a database, or execute native/provider code.

`OpenApiGenerateDocuments=false` remains set. No Microsoft.Extensions.ApiDescription.Server,
GetDocument, app-executing build target, or runtime endpoint probe is installed or
used. The runtime registered-route document remains unexercised under the requested
no-app/no-API verification restriction.
