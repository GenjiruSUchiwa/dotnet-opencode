namespace OpenCode.Client;

using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using OpenCode.Protocol;
using OpenCode.Schema;

internal sealed record ServiceContender(SafeProcessHandle Process, string Nonce, string DiagnosticPath) : IDisposable
{
    private ProcessExitStatus? _exitStatus;
    internal ProcessExitStatus? ExitStatus
    {
        get
        {
            if (_exitStatus is null && Process.TryWaitForExit(TimeSpan.Zero, out var status)) _exitStatus = status;
            return _exitStatus;
        }
    }
    public void Dispose() => Process.Dispose();
}

internal static class ServiceStartupDiagnostics
{
    internal static bool IsCanonicalNonce(string nonce) => Guid.TryParseExact(nonce, "N", out var parsed)
        && string.Equals(parsed.ToString("N"), nonce, StringComparison.Ordinal);

    internal static string PathFor(string nonce)
    {
        if (!IsCanonicalNonce(nonce)) throw new ArgumentException("Startup nonce must be a lowercase canonical GUID.", nameof(nonce));
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } root
            ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.GetFullPath(Path.Combine(state, "opencode", "service-" + OpenCodeChannel.Name + "-startup", nonce + ".json"));
    }

    internal static string Create(string nonce, string entry)
    {
        var path = PathFor(nonce);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (path.StartsWith(Path.GetDirectoryName(entry)! + Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
            || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part =>
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration,
                "Startup diagnostics must be outside deployment and build directories.", "Choose an absolute XDG_STATE_HOME outside bin/obj and immutable snapshots.");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = PrivateFile(path);
            JsonSerializer.Serialize(stream, new ServiceStartupDiagnostic(nonce, OpenCodeChannel.Application, "pending", "launch", 0),
                ServiceStartupJsonContext.Default.ServiceStartupDiagnostic);
            stream.Flush(flushToDisk: true);
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration,
                $"Private startup diagnostics could not be created (code {error.HResult}). Expected path: {path}",
                "Check XDG_STATE_HOME and channel-state permissions. No service process was started.", error, path);
        }
    }

    private static FileStream PrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
        }
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
    }

    internal static async Task<ServiceLifecycleException> FailureAsync(string nonce, int pid, ServiceFailure code, CancellationToken ct)
    {
        var path = PathFor(nonce);
        ServiceStartupDiagnostic? report = null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            if (stream.Length <= 32 * 1024)
                report = await JsonSerializer.DeserializeAsync(stream, ServiceStartupJsonContext.Default.ServiceStartupDiagnostic, ct).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        var matches = report is not null && report.Nonce == nonce && report.Application == OpenCodeChannel.Application && report.Pid == pid && pid > 0;
        if (matches && report is { State: "failed", Code: not null }
            && !string.IsNullOrWhiteSpace(report.Summary) && !string.IsNullOrWhiteSpace(report.Action)
            && report.Stage is "entry" or "configuration" or "host-build" or "host-start" or "election" or "listen" or "registration" or "storage")
            return new ServiceLifecycleException(code,
                $"Service startup failed during {report.Stage}: {report.Summary} {report.Action} Diagnostic: {path}",
                report.Action!, diagnosticPath: path);
        var observed = code == ServiceFailure.StartupFailed
            ? "The authenticated service reports failed startup, but no matching failure details were available."
            : matches && report!.State == "ready"
                ? "The contender recorded startup readiness before it exited; the final exit is not explained by its startup record."
                : matches && report!.State == "starting"
                    ? "The managed entrypoint acknowledged startup, but final failure details were not recorded."
                    : "The contender exited without a matching managed startup failure record.";
        var action = matches && report!.State == "ready"
            ? "Inspect the verified instance's runtime or shutdown diagnostics. Its startup record does not capture post-readiness errors; no replacement was attempted."
            : report is { State: "pending", Stage: "launch", Pid: 0 }
            && report.Nonce == nonce && report.Application == OpenCodeChannel.Application
                ? "No managed startup acknowledgement was recorded. Verify the pinned .NET 11 preview runtime, complete updated Server package, and diagnostic-file permissions."
                : "Check the private report's permissions and attempt identity. Missing, malformed, and stale reports are not used as failure evidence; do not reuse startup nonces.";
        return new ServiceLifecycleException(code,
            $"{observed} Diagnostic: {path}", action, diagnosticPath: path);
    }
}
