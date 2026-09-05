# Native Login Handoff

`AuthCommands.RunAsync(args, cancellationToken)` takes the full argument array.
It returns `AuthCommandResult` with an exit code and postcommit credential
notification descriptors. It does not change `Environment.ExitCode` itself.

The main entry point owner can route commands before starting the TUI or SDK:

```csharp
if (args.Length > 0 && args[0] is "auth" or "console")
{
    using var lifetime = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) =>
    {
        e.Cancel = true;
        lifetime.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        var result = await OpenCode.Cli.Auth.AuthCommands.RunAsync(args, lifetime.Token);
        Environment.ExitCode = result.ExitCode;
        // result.Notifications awaits real credential-bus integration.
    }
    finally { Console.CancelKeyPress -= cancel; }
    return;
}
```

`Program.cs` is intentionally unchanged. The commands are not reachable through
the installed main entry point until its owner adds routing.

## Supported Commands

```text
console login [url]
auth login opencode [--method device]
auth login openai --method chatgpt-browser
auth login openai --method chatgpt-headless
auth login
auth --help
```

The last login form selects the integration and method interactively. Redirected
input requires explicit selection. Method labels from the source are also
accepted. Unsupported API-key, plugin, discovery, remote-server, list, and logout
commands fail rather than silently selecting another flow.

Source references: `packages/cli/src/commands/commands.ts`,
`handlers/auth/login.ts`, `handlers/auth/shared.ts`,
`handlers/console/login.ts`, and `packages/core/src/plugin/provider/openai.ts`.

## Ownership And Cleanup

Login uses the existing `SqliteDatabase` default constructor and connection
bootstrap, selecting the dotnet channel database. There is no shared-auth import,
database-path override, daemon startup, or model resolution. Help and invalid
arguments do not open the database.

The browser callback uses an owned `HttpListener` on localhost, port 1455 then
1457, with bounded bind retries. Unlike the source takeover behavior, it never
sends `/cancel` to another process. It validates the callback path, method,
loopback peer, unique state and code parameters, and the original PKCE state.
Invalid-state requests do not consume the pending attempt. Error text from the
provider is never echoed. The browser response acknowledges receipt, not a
successful credential save; the terminal reports completion after persistence.

Cancellation stops and settles the pending listener accept. The listener, failed
bind attempts, HTTP client, database, deadline, and cancellation registration
are scoped. No background prompt reader or polling task remains. External
browsers are user-owned: the launcher handle is disposed without killing them.
Headless mode never launches a browser. Browser and Console flows open one only
with interactive input and output; otherwise they print the intended URL.

## Notification Gap

Credential persistence returns the real `CredentialMutation.Notifications`.
The CLI returns these descriptors without printing credential values or labels.
`SessionEvents` is a process-local session notification mechanism, not the
source credential bus and not a route to a separate running server. No fake
session event or standalone bus is created here. Live server catalog refresh
after login still requires integration with the real credential event path.

Exit codes: 0 for completion/help, 2 for invalid or unsupported arguments,
130 for user cancellation, and 1 for expiry or authentication/storage failure.
Only builds have been used for verification; listener binding, OAuth exchanges,
browser launch, persistence, and cancellation have not been runtime-tested.
