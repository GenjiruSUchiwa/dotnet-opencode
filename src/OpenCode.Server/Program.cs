using OpenCode.Server;

StartupDiagnostics? diagnostics = null;
try
{
    diagnostics = StartupDiagnostics.Begin(args);
    await using var app = ServerHost.CreateApp(args, diagnostics);
    diagnostics.Phase("host-start");
    await app.RunAsync();
}
catch (ServiceAlreadyRunningException)
{
    // A losing contender leaves the elected server and its registration untouched.
    diagnostics?.Complete("incumbent");
}
catch (Exception error)
{
    var failure = diagnostics?.Fail(error);
    Console.Error.WriteLine(failure is null
        ? "Unable to initialize private startup diagnostics. Verify channel state permissions and startup arguments."
        : $"Unable to start the .NET service: {failure.Summary} {failure.Action} Diagnostic: {diagnostics!.Path}");
    Environment.ExitCode = 1;
}

public partial class Program { }
