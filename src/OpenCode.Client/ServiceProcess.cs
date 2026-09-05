namespace OpenCode.Client;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenCode.Schema;
using OpenCode.Protocol;

public sealed record ServiceStartOptions : ServiceDiscoveryOptions
{
    /// <summary>Null uses the server's channel config, falling back to port 5055.</summary>
    public int? Port { get; init; }
    public TimeSpan StartupTimeout { get; init; } = ServiceTiming.StartupTimeout;
    /// <summary>Dotnet plus a built assembly, or a built apphost, followed by service flags. Runtime assets are snapshotted before launch.</summary>
    public IReadOnlyList<string>? Command { get; init; }
    /// <summary>Permit one authenticated replacement: idle-only for published builds, strictly older timestamps for dotnet-local. Explicit servers are never replaced.</summary>
    public bool ReplaceIncompatible { get; init; } = true;
}

internal static class ServiceProcess
{
    internal static ServiceContender Start(IReadOnlyList<string>? command, string file, int? port, string expectedBuildId, PersistentPtyHandoff? ptyHandoff = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Automatic service launch supports Windows and Linux. Start the server explicitly on this platform and use discovery.");
        foreach (var name in new[] { "OPENCODE_CONFIG_DIR", "OPENCODE_DATA_DIR", "OPENCODE_CONFIG",
            "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME" })
        {
            if (Environment.GetEnvironmentVariable(name) is not { Length: > 0 } path || Path.IsPathFullyQualified(path)) continue;
            throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration,
                $"{name} must be an absolute path for managed daemon launch.",
                "Resolve the override against the caller's project directory or remove it to use the channel defaults. No deployment was staged and no global environment variable was changed.");
        }
        command = ResolveCommand(command);
        ValidateBuild(command, expectedBuildId);
        var configFile = Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVICE_CONFIG");
        for (var index = 1; index < command.Count; index++)
        {
            if (command[index] != "--service-config") continue;
            if (++index == command.Count) throw new ArgumentException("Missing --service-config path.", nameof(command));
            configFile = command[index];
        }
        if (configFile is not null) configFile = Path.GetFullPath(configFile);

        var hostName = Path.GetFileName(command[0]);
        var dotnet = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var entry = ServiceDeployment.Snapshot(dotnet ? command[1] : command[0]);
        ValidateBuild(dotnet ? [command[0], entry] : [entry], expectedBuildId);
        var executable = dotnet
            ? command[0].IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                ? Path.GetFullPath(command[0]) : DotnetHost()
            : entry;

        // Explicit null handles break the stdio inheritance chain as well as
        // excluding all non-standard handles. The daemon must survive its client.
        using var nullHandle = File.OpenNullHandle();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            StartDetached = true,
            InheritedHandles = [],
            StandardInputHandle = nullHandle,
            StandardOutputHandle = nullHandle,
            StandardErrorHandle = nullHandle,
            WorkingDirectory = Path.GetDirectoryName(entry)!
        };
        if (dotnet) start.ArgumentList.Add(entry);
        if (ptyHandoff is not null)
            start.Environment["OPENCODE_DOTNET_PTY_HANDOFF"] = JsonSerializer.Serialize(ptyHandoff, OpenCodeJsonContext.Default.PersistentPtyHandoff);
        else start.Environment.Remove("OPENCODE_DOTNET_PTY_HANDOFF");
        foreach (var arg in command.Skip(dotnet ? 2 : 1)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--service");
        if (port is int selectedPort)
        {
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(selectedPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("--registration-file");
        start.ArgumentList.Add(file);
        if (configFile is not null)
        {
            start.ArgumentList.Add("--service-config");
            start.ArgumentList.Add(configFile);
        }
        var nonce = Guid.NewGuid().ToString("N");
        var diagnostic = ServiceStartupDiagnostics.Create(nonce, entry);
        start.ArgumentList.Add("--startup-id");
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add("--startup-report");
        start.ArgumentList.Add(diagnostic);
        try
        {
            return new ServiceContender(SafeProcessHandle.Start(start), nonce, diagnostic);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var code = error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : error.HResult;
            throw new ServiceLifecycleException(ServiceFailure.ContenderFailed,
                $"The operating system could not launch the service (code {code}). Verify the executable and pinned .NET 11 preview runtime. Diagnostic: {diagnostic}",
                "No managed entrypoint ran and no incumbent was stopped. The private pending record identifies this launch attempt.", error, diagnostic);
        }
    }

    internal static void ValidateBuild(IReadOnlyList<string>? command, string expected)
    {
        command = ResolveCommand(command);
        var hostName = Path.GetFileName(command[0]);
        var dotnet = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        var entry = Path.GetFullPath(dotnet ? command[1] : command[0]);
        var stamp = Path.Combine(Path.GetDirectoryName(entry)!, "opencode-build.id");
        if (!File.Exists(stamp) || File.ReadAllText(stamp).Trim() != expected)
            throw new ServiceLifecycleException(ServiceFailure.IncompatibleBuild, "The selected Server package does not match this application's build fingerprint.",
                "Run ./run.ps1 to rebuild and package matching CLI/Server assets. No incumbent was stopped.");
        if (OpenCodeChannel.IsLocal)
        {
            var timestamp = Path.Combine(Path.GetDirectoryName(entry)!, "opencode-build.timestamp");
            if (!File.Exists(timestamp) || !long.TryParse(File.ReadAllText(timestamp).Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) || value != ApplicationBuild.Timestamp)
                throw new ServiceLifecycleException(ServiceFailure.IncompatibleBuild, "The selected Server package has a different local build timestamp.",
                    "Use the complete matching ./run.ps1 build. No incumbent was stopped.");
        }
    }

    private static IReadOnlyList<string> ResolveCommand(IReadOnlyList<string>? command)
    {
        var configured = Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVICE_COMMAND");
        command ??= configured is null ? null : JsonSerializer.Deserialize<string[]>(configured);
        if (command is null)
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "server", "OpenCode.Server.dll");
            var assembly = File.Exists(packaged) ? packaged : Path.Combine(AppContext.BaseDirectory, "OpenCode.Server.dll");
            if (!File.Exists(assembly) || !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")))
                throw new InvalidOperationException("No packaged .NET server was found. Supply ServiceStartOptions.Command or OPENCODE_DOTNET_SERVICE_COMMAND as a JSON array of executable and arguments.");
            command = ["dotnet", assembly];
        }
        if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
            throw new ArgumentException("The service command must contain an executable.", nameof(command));

        var hostName = Path.GetFileName(command[0]);
        var dotnet = hostName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || hostName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
        if (dotnet && (command.Count < 2 || !command[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Persistent service launch requires dotnet <built-server.dll>. dotnet run, project builds, and dotnet exec overrides are not supported.", nameof(command));
        return command;
    }

    private static string DotnetHost()
    {
        var configured = Environment.GetEnvironmentVariable("OPENCODE_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null)
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration, "The selected .NET host path is not an existing absolute file.",
                    "Use run.ps1 with the pinned local SDK or set OPENCODE_DOTNET_HOST to its dotnet executable.");
            return configured;
        }
        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return current;
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        if (runtime.Parent?.Name == "Microsoft.NETCore.App" && runtime.Parent.Parent?.Name == "shared")
        {
            var candidate = Path.Combine(runtime.Parent.Parent.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        throw new ServiceLifecycleException(ServiceFailure.InvalidConfiguration, "A matching .NET host could not be resolved for daemon startup.",
            "Use run.ps1, specify OPENCODE_DOTNET_HOST, or supply a complete apphost deployment. System .NET 10 is not a fallback.");
    }
}
