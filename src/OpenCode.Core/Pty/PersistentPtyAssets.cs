namespace OpenCode.Core.Pty;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

public sealed record PersistentPtyDeployment(string RuntimeIdentifier, bool PlatformArtifactAvailable,
    bool Packaged, bool OverrideConfigured, bool CanAttempt, string? Version, string? Reason)
{
    public bool AutomaticLaunchSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
}

/// <summary>Offline resolution only: explicit native override, verified packaged asset, then native PATH candidate.</summary>
public static class PersistentPtyAssets
{
    public static PersistentPtyDeployment Describe(string? configured = null)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        using var pins = Pins();
        var published = pins.RootElement.GetProperty("targets").TryGetProperty(rid, out _);
        var explicitPath = Override(configured);
        try
        {
            var path = Resolve(configured);
            var packaged = explicitPath is null && path.StartsWith(Path.Combine(AppContext.BaseDirectory, "native", "opencode-pty") + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var launch = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
            return new(rid, published, packaged, explicitPath is not null, launch,
                packaged ? pins.RootElement.GetProperty("version").GetString() : null,
                !launch ? "This .NET host currently implements automatic daemon launch on Windows/Linux only; published macOS assets alone do not establish launch support."
                    : published ? "Native protocol/platform execution has not been verified by this capability query."
                    : "Upstream publishes no artifact for this platform; the explicit or PATH native daemon is unverified.");
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException or JsonException)
        {
            return new(rid, published, false, explicitPath is not null, false, null, error.Message);
        }
    }

    public static string Resolve(string? configured = null)
    {
        if (Override(configured) is { } specified)
            return Native(Find(specified) ?? throw new FileNotFoundException("The explicitly configured persistent PTY executable is unavailable."), allowLink: true);
        using var pins = Pins();
        var rid = RuntimeInformation.RuntimeIdentifier;
        var directory = Path.Combine(AppContext.BaseDirectory, "native", "opencode-pty", rid);
        if (pins.RootElement.GetProperty("targets").TryGetProperty(rid, out var target) && Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != (FileAttributes)0) throw new IOException("Packaged PTY assets cannot be linked directories.");
            var binary = Path.Combine(directory, "opencode-pty");
            Check(binary, target.GetProperty("sha256").GetString()!);
            Check(Path.Combine(directory, "package.json"), target.GetProperty("packageSha256").GetString()!);
            Check(Path.Combine(directory, "LICENSE"), pins.RootElement.GetProperty("licenseSha256").GetString()!);
            return Native(binary);
        }
        if (Find("opencode-pty") is { } installed) return Native(installed, allowLink: true);
        if (OperatingSystem.IsWindows())
            throw new NotSupportedException("Upstream opencode-pty 0.1.13 has no Windows artifact or named-pipe daemon. Use ordinary ConPTY terminals; an explicit custom persistent daemon is unverified.");
        throw new FileNotFoundException("Persistent PTY native assets are not packaged. Build/publish with OpenCodePackagePersistentPty=true for a supported target, or supply an explicit native executable. No runtime download was attempted.");
    }

    private static string? Override(string? configured) => !string.IsNullOrEmpty(configured) ? configured
        : Environment.GetEnvironmentVariable("OPENCODE_DOTNET_PTY_BIN") is { Length: > 0 } dotnet ? dotnet
        : Environment.GetEnvironmentVariable("OPENCODE_PTY_BIN") is { Length: > 0 } source ? source : null;

    private static string? Find(string executable)
    {
        if (Path.IsPathFullyQualified(executable)) return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        if (executable.Contains('/') || executable.Contains('\\')) return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var root = entry.Trim('"');
            if (!Path.IsPathFullyQualified(root)) continue;
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() && !Path.HasExtension(executable) ? executable + ".exe" : executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string Native(string file, bool allowLink = false)
    {
        if (allowLink && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != (FileAttributes)0)
            file = File.ResolveLinkTarget(file, true)?.FullName ?? throw new IOException("The explicit native executable link cannot be resolved.");
        if ((File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != (FileAttributes)0)
            throw new IOException("Persistent PTY executables must be regular native files, not links or launchers.");
        using var input = File.OpenRead(file);
        Span<byte> magic = stackalloc byte[4];
        if (input.Read(magic) != 4 || !(magic.SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 })
            || magic.SequenceEqual(new byte[] { 0xcf, 0xfa, 0xed, 0xfe }) || magic.SequenceEqual(new byte[] { 0xfe, 0xed, 0xfa, 0xcf })
            || magic.SequenceEqual(new byte[] { 0xca, 0xfe, 0xba, 0xbe }) || (magic[0] == 'M' && magic[1] == 'Z')))
            throw new NotSupportedException("Persistent PTY resolution requires a native executable, not the npm JavaScript launcher, Bun, Node, or a script shim.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == (UnixFileMode)0)
            throw new IOException("The packaged persistent PTY file is not executable. Preserve its executable mode when distributing the explicit build output.");
        return file;
    }

    private static void Check(string file, string expected)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != (FileAttributes)0) throw new IOException("Packaged PTY assets cannot be linked files.");
        using var input = File.OpenRead(file);
        if (Convert.ToHexStringLower(SHA256.HashData(input)) != expected) throw new IOException("Packaged persistent PTY asset failed its pinned SHA-256 check.");
    }

    private static JsonDocument Pins()
    {
        using var stream = typeof(PersistentPtyAssets).Assembly.GetManifestResourceStream("OpenCode.Pty.AssetPins.json")
            ?? throw new InvalidOperationException("The persistent PTY asset pins are not embedded in this build.");
        return JsonDocument.Parse(stream);
    }
}
