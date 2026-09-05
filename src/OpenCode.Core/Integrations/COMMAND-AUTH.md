# Command authentication

The three canonical command connect/status/cancel routes use the Location-owned
`IntegrationRuntime`. The Client already exposes typed methods and Schema already
defines the command payload, attempt, and status union; these contracts are unchanged.

Native hosts register `IIntegrationCommandSource` through DI. Its typed registrations
are merged with real provider/MCP definitions on Location reload, replacing command
methods by integration ID and method ID. Core-only embeddings can use `Define`.
No built-in command method is invented. JavaScript plugin configuration is not read
as executable registrations; those sources remain unsupported until a plugin runtime
actually loads them. Thus a stock catalog need not contain a command method even
though the HTTP lifecycle is implemented.

Source: `packages/core/src/integration.ts`, command registration, `connectCommand`,
`settleCommand`, `scrubCommands`, and command status/cancel. The source runs argv
directly with inherited environment and ignored stdin. It does not use Forms or PTY
for command authentication, so this implementation does not add either.

- Each attempt owns its process, concurrent UTF-8 stdout/stderr readers, cancellation,
  and settlement task. Cancellation kills the owned process tree and waits for readers
  and the root to settle; no process-name lookup or unrelated process termination.
- Pending status accumulates stderr. Stdout is private credential material, never a
  status field or event payload. Source stderr may itself contain sensitive output;
  authenticated callers receive it as required by the source contract.
- Exit code zero alone is not authentication. Trimmed stdout must be nonempty and
  become a committed `CredentialKey` in the existing channel `CredentialStore`.
  There is no extra provider verification step in the source command contract.
- Explicit labels are preserved; omitted labels use the existing unique-label rule.
  Only secret-free credential notifications are published after commit.
- Attempts expire after 10 minutes, checked every 30 seconds. Terminal status remains
  for 1 minute. Expiry and cancellation cannot claim an attempt once persistence has
  started; that commit and its notification finish without request cancellation.
- Cancel removes a matching pending attempt, then waits for owned cleanup. Unknown,
  cross-integration, terminal, or persisting attempts are cancellation no-ops.
  Status lookup rejects unknown or cross-integration IDs.
- Location/host disposal cancels and joins command workers, including workers whose
  attempt was removed by a concurrent cancellation request.

The native endpoint filter maps missing command/attempt errors to InvalidRequestError,
consistent with its existing integration error boundary. Operational failures that
are not source command-exit/empty-output errors use a generic failure message rather
than raw process/persistence exceptions.

Wellknown discovery and plugin listing remain separate unavailable handlers.
Verification is build-only: no auth command, listener, process, PTY, database, HTTP
request, or runtime was executed. `OpenApiGenerateDocuments=false` remains enabled.
