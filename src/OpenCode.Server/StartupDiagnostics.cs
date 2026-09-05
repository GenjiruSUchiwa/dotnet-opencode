namespace OpenCode.Server;

using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OpenCode.Core.Database;
using OpenCode.Protocol;
using OpenCode.Schema;

/// <summary>File-backed equivalent of contender stderr capture, without raw exception text or inherited pipes.</summary>
internal sealed class StartupDiagnostics : ILoggerProvider
{
    private readonly Lock _gate = new();
    private ServiceStartupDiagnostic _report;
    private readonly bool _persist;
    internal string Path { get; }
    internal string Nonce => _report.Nonce;

    private StartupDiagnostics(string path, ServiceStartupDiagnostic report, bool persist = true) { Path = path; _report = report; _persist = persist; }

    internal static StartupDiagnostics InMemory() => new("", new ServiceStartupDiagnostic(Guid.NewGuid().ToString("N"),
        OpenCodeChannel.Application, "starting", "entry", Environment.ProcessId), persist: false);

    internal static StartupDiagnostics Begin(string[] args)
    {
        string? nonce = null;
        string? suppliedPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] is not ("--startup-id" or "--startup-report")) continue;
            var name = args[index];
            if (++index == args.Length) throw new ArgumentException("Missing startup diagnostic argument.");
            if (name == "--startup-id") nonce = args[index];
            if (name == "--startup-report") suppliedPath = args[index];
        }
        if ((nonce is null) != (suppliedPath is null)) throw new ArgumentException("Startup ID and report must be supplied together.");
        nonce ??= Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(nonce, "N", out var parsed) || parsed.ToString("N") != nonce)
            throw new ArgumentException("Startup nonce must be a lowercase canonical GUID.");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } root
            ? root : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(state, "opencode", "service-" + OpenCodeChannel.Name + "-startup", nonce + ".json"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((suppliedPath is not null && (!System.IO.Path.IsPathFullyQualified(suppliedPath) || !path.Equals(System.IO.Path.GetFullPath(suppliedPath), comparison)))
            || path.StartsWith(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, comparison)
            || path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Startup diagnostics must use the private channel state directory, outside deployment and build output.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (suppliedPath is not null && !File.Exists(path))
            throw new InvalidDataException("The caller-owned pending startup report is missing.");
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 32 * 1024) throw new InvalidDataException("Invalid startup diagnostic record.");
            var previous = JsonSerializer.Deserialize(stream, ServiceStartupJsonContext.Default.ServiceStartupDiagnostic);
            if (previous is null || previous.Nonce != nonce || previous.Application != OpenCodeChannel.Application
                || previous.Pid != 0 || previous.State != "pending" || previous.Stage != "launch")
                throw new InvalidDataException("Startup diagnostic nonce has already been claimed or belongs to another attempt.");
        }
        var diagnostics = new StartupDiagnostics(path,
            new ServiceStartupDiagnostic(nonce, OpenCodeChannel.Application, "starting", "entry", Environment.ProcessId));
        diagnostics.Save();
        return diagnostics;
    }

    internal void Phase(string stage)
    {
        lock (_gate)
        {
            if (_report.State != "starting") return;
            _report = _report with { Stage = stage };
            TrySave();
        }
    }

    internal void Complete(string state)
    {
        lock (_gate)
        {
            if (_report.State == "failed") return;
            _report = _report with { State = state, Stage = state };
            TrySave();
        }
    }

    internal ServiceStartupDiagnostic Fail(Exception error)
    {
        lock (_gate)
        {
            if (_report.State is "failed" or "ready" or "incumbent") return _report;
            var chain = new List<Exception>();
            for (Exception? current = error; current is not null && chain.Count < 8; current = current.InnerException) chain.Add(current);
            var reason = Describe(chain, _report.Stage);
            _report = _report with
            {
                State = "failed", Code = reason.Code, Summary = reason.Summary, Action = reason.Action,
                ExceptionTypes = chain.Select(item => SafeType(item.GetType())).ToArray(),
                HResults = chain.Select(item => item.HResult).ToArray(),
                Frames = chain.SelectMany(item => new StackTrace(item, false).GetFrames() ?? [])
                    .Select(frame => frame.GetMethod()).OfType<MethodBase>()
                    .Where(method => method.DeclaringType is not null && SafeType(method.DeclaringType) != "ExternalException")
                    .Select(method => SafeType(method.DeclaringType!) + "." + method.Name)
                    .Where(name => name.Length <= 240).Distinct().Take(12).ToArray()
            };
            TrySave();
            return _report;
        }
    }

    private static (string Code, string Summary, string Action) Describe(IReadOnlyList<Exception> chain, string stage)
    {
        if (chain.Any(item => item is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
            || item.GetType().FullName == "Microsoft.AspNetCore.Connections.AddressInUseException"))
            return ("address-in-use", "The configured listener address is already in use.", "Choose a free .NET service port or explicitly stop the verified incumbent; no process was killed.");
        if (chain.Any(item => item is DatabaseSchemaUnavailableException))
            return ("database-schema", "The channel database does not match the supported schema baseline.", "Use the supported fresh channel bootstrap or implement the required migration; do not copy a live database or fabricate history.");
        if (chain.OfType<SqliteException>().LastOrDefault() is { } sqlite)
            return ("database-open", $"SQLite initialization failed with code {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode}.", "Check the channel data directory permissions and schema baseline. Database contents and SQL were not logged.");
        if (chain.Any(item => item is UnauthorizedAccessException))
            return ("access-denied", "Access was denied during startup.", "Check permissions on the channel state, service config, deployment cache, and data directories; keep writable state outside the snapshot.");
        if (chain.OfType<JsonException>().LastOrDefault() is { } json)
            return ("invalid-json", $"Startup JSON could not be decoded (line {json.LineNumber}, byte {json.BytePositionInLine}).", "Check the channel service configuration or registration for the reported phase. JSON values were not logged.");
        if (chain.Any(item => item is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException))
            return ("runtime-assets", "A managed or native runtime dependency could not be loaded.", "Rebuild the complete matching-architecture Server package, including deps/runtimeconfig and native runtime assets; verify the pinned .NET 11 preview runtime.");
        foreach (var item in chain.OfType<InvalidOperationException>())
        {
            if (item.TargetSite?.DeclaringType?.Namespace?.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) == true
                && (item.Message.StartsWith("Unable to resolve service for type '", StringComparison.Ordinal)
                || item.Message.StartsWith("Unable to activate type '", StringComparison.Ordinal)
                || item.Message.StartsWith("A suitable constructor for type '", StringComparison.Ordinal)))
            {
                // Extract only compiled type identifiers from known DI diagnostics, never arbitrary quoted values.
                var types = Regex.Matches(item.Message[..Math.Min(item.Message.Length, 4096)],
                    "'((?:OpenCode|Microsoft|System)\\.[A-Za-z0-9_.+`\\[\\], ]+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    .Select(match => match.Groups[1].Value).Distinct().Take(3).ToArray();
                return ("dependency-injection", types.Length == 0 ? "Host dependency construction failed; see the sanitized call sites."
                        : "Host dependency construction failed: " + string.Join(", ", types) + ".",
                    "Register the actual dependency or correct constructor selection. Do not bypass execution capability checks.");
            }
            if (item.Message.StartsWith("Body was inferred but the method does not allow inferred body parameters", StringComparison.Ordinal))
                return ("endpoint-binding", "A route inferred a body parameter on an HTTP method that forbids it.", "Check endpoint DI registration and explicit parameter binding; do not enable inferred GET bodies.");
        }
        if (stage == "configuration")
            return ("service-configuration", "Channel service configuration or arguments are invalid.", "Check service-dotnet.json, port/hostname/password field types, and the supplied service flags. Values were not logged.");
        if (stage == "election")
            return ("service-election", "The registration/election check could not safely proceed.", "Verify the registered .NET instance and its private state permissions. Automatic replacement is unsupported.");
        return ("host-startup", "The host failed during startup; sanitized exception types, error codes, and call sites are recorded.",
            "Use the private diagnostic record to identify the failing component. Raw exception messages and configuration values were deliberately omitted.");
    }

    private static string SafeType(Type type)
    {
        var name = type.FullName ?? type.Name;
        return name.Length <= 240 && (name.StartsWith("OpenCode.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal))
            ? name : "ExternalException";
    }

    private void Save()
    {
        if (!_persist) return;
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = ServiceLifetime.CreatePrivateFile(temp))
            {
                JsonSerializer.Serialize(stream, _report, ServiceStartupJsonContext.Default.ServiceStartupDiagnostic);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("A private startup diagnostic temporary file could not be removed; published reports were not changed by cleanup."); }
        }
    }

    private void TrySave()
    {
        try { Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Private startup diagnostics could not be updated; check channel state permissions."); }
    }

    public ILogger CreateLogger(string categoryName) => new StartupLogger(this,
        categoryName == "Microsoft.Extensions.Hosting.Internal.Host");
    public void Dispose() { }

    private sealed class StartupLogger(StartupDiagnostics owner, bool capture) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => capture && logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (capture && logLevel >= LogLevel.Error && exception is not null) owner.Fail(exception);
        }
    }
}
