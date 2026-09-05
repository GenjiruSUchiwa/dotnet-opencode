# Private in-process host

`ServerHost.CreateStandaloneApp(string password)` returns the real WebApplication
composition with a separate standalone lifetime. The caller must provide a nonempty
ephemeral credential and own StartAsync/StopAsync/DisposeAsync.

- IPv4 loopback port 0, ignoring managed service port/config overrides.
- Actual bound URL through IServerAddressesFeature; authenticated health shares
  normal native version/build/instance fields.
- StartAsync completes only after storage initialization and address validation;
  failure propagates rather than leaving a guessed ready URL.
- Normal dotnet-channel database/configuration, not an empty or copied database.
- No managed ServiceLifetime registration, election, incumbent inspection, service
  config/registration writes, registration monitor, or managed stop endpoint.
- No consumption/clearing of managed PTY handoff environment and no startup shell,
  subagent, or root claim recovery.
- In-memory startup diagnostics, stderr logging, and no ConsoleLifetime signal hooks.
- Private disposal joins its owned work; the execution service's no-recovery path
  does not sweep or interrupt arbitrary process-global Session owners.

CreateApp remains the managed factory. Both factories share the API/service graph
and credential verifier; CLI must not use reflection or remove hosted services to
manufacture standalone mode. No WebApplication was created/started as verification;
only the pinned .NET build was run.
