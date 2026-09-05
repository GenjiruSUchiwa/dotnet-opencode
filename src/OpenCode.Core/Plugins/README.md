# Native Location plugin host

This is a native contribution boundary, **not an external JavaScript plugin loader**.
The tool Location owns `NativePluginHost` beside its registry and MCP runtime. The
plugin route acquires that same Location and returns its actual initialization
inventory. It does not turn config declarations into active entries.

## Implemented boundary

- Existing native tool producers register through real `NativePluginDefinition`
  initializers and setup-scoped `TransformTools` registrations. Only installed tools
  appear. IDs are the corresponding `opencode.tool.*` source IDs; version `native`
  identifies the implementation, not execution of the TypeScript internal plugin.
- Supported builtins follow the relative order in `PluginInternal.pre`: edit, glob,
  grep, question, read, shell, skill, subagent, webfetch, write. Optional tools are
  absent when their actual services are absent. No placeholder patch/websearch or
  provider/config plugins are advertised.
- Explicit native `INativePluginSource` registrations follow builtins in supplied
  order. They default to builtin provenance; SDK contributions should explicitly use
  `PluginSourceSdk`. No discovery, package import, DI startup, or execution happens
  while defining these APIs.
- A stable registry transform preserves this producer position ahead of MCP.
  Setup stages contributions; generation commit rebuilds the existing ToolRegistry
  in one synchronous batch. Tool hooks run sequentially in registration order through
  the actual `IToolExecutionHooks` invocation path, after any inherited host hooks.
- Setup scopes own lifetime tokens and async resources. Failed setup unwinds every
  registered resource in reverse order. A failed replacement attempts initialization
  of the previous definition again, while inventory records the failed requested
  generation, matching source recovery vocabulary. Removed plugins close in reverse
  activation order. Generation commit failure tears down staged contributions rather
  than retaining an active claim for an invalid catalog.
- Identical ordered active IDs/versions do not initialize again. Cancellation and
  generation failure clean up owned scopes. Location disposal closes plugin resources
  before the registry. Individual transform disposal rebuilds the native catalog.
- `plugin.added` carries only `{ id }`; `plugin.updated` carries `{}`. Both are
  ephemeral Location events, with no persisted lifecycle or copied plugin state.

`NativePluginScope` deliberately permits registration during setup only. Resources
must be registered with `Own` before later initialization work can fail. A resource
that starts owned work must join it during its async disposal. There is no implicit
fire-and-forget scheduler or blanket service-provider escape hatch.

## Remaining compatibility, not claimed

Source references: `plugin.ts`, `plugin/supervisor.ts`, `plugin/host.ts`,
`plugin/hooks.ts`, `plugin/internal.ts`, `plugin/module.ts`, and `state.ts`.
The source supports more than this initial native host:

- Supervisor config add/remove selectors, filesystem/package observation, pre / SDK /
  instance / external / post ordering, and coalesced asynchronous generation reload.
  Native sources are captured during Location construction; there is no config watcher
  or complete internal-post generation implementation here.
- Scoped transforms for agents, providers/models, commands, integrations, MCP config,
  references, skills, VCS, and websearch; session, shell, permission, and AI SDK hooks
  with source-specific payloads/provider filters and typed failure channels. Only the
  existing native tool transform/hook boundary is connected in this batch.
- Plugin-namespaced durable KV, full cross-Location host operations, runtime services,
  public event subscriptions, and the Effect/Promise plugin-context adapters.
- Compatible JS/TS module evaluation, package exports/server entrypoint resolution,
  dependency installation/cache semantics, Node builtins, async cancellation/scopes,
  Effect service semantics, and any requested native addon ABI. Jint's existing
  code-mode evaluator supplies none of these guarantees by itself. No Node/Bun process,
  new JavaScript runtime, silent C# substitute, or package loading was added.

Configured/discovered-JS execution guards remain unchanged. Instructions still use
the existing explicit instruction composition; no instruction registry was added.
Catalog success covers actual native instances only, not overall plugin parity.
Wellknown discovery is owned separately and is not implemented here.

Verification is source inspection and the pinned .NET Server build only, with
`OpenApiGenerateDocuments=false`. No host/DI startup, plugin setup, hook, JavaScript,
native code, database, process, network, or test execution was used for verification.
