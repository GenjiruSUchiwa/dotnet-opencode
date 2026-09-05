namespace OpenCode.Client;

using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using OpenCode.Schema;

/// <summary>Stages complete runtime assets without holding mutable build outputs open for the daemon lifetime.</summary>
internal static class ServiceDeployment
{
    internal static string Snapshot(string entryPoint)
    {
        entryPoint = Path.GetFullPath(entryPoint);
        var source = Path.GetDirectoryName(entryPoint)!;
        var name = Path.GetFileName(entryPoint);
        var extension = Path.GetExtension(name);
        var stem = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name) : name;
        if (!File.Exists(entryPoint) || !File.Exists(Path.Combine(source, stem + ".dll"))
            || !File.Exists(Path.Combine(source, stem + ".deps.json"))
            || !File.Exists(Path.Combine(source, stem + ".runtimeconfig.json")))
            throw new InvalidOperationException("The service requires a complete multi-file build or publish output: entry point, DLL, deps.json, runtimeconfig.json, and runtime dependencies. Source-project commands and single-file bundles are not supported.");

        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var root = Path.GetFullPath(Path.Combine(cache, "opencode", "service-" + OpenCodeChannel.Name, "deployments"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (root.StartsWith(source + Path.DirectorySeparatorChar, comparison)
            || root.Equals(source, comparison)
            || root.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The service deployment cache must be outside the source runtime directory and mutable bin/obj directories.");

        var files = ReadManifest(source);
        var manifest = ManifestText(files);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        var destination = Path.Combine(root, digest);
        if (Directory.Exists(destination))
        {
            RequireMatchingSnapshot(destination, manifest);
            return Path.Combine(destination, name);
        }

        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(source, file.Path), target);
                if (OperatingSystem.IsWindows()) File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)file.Mode);
            }
            RequireMatchingSnapshot(staging, manifest);
            if (ManifestText(ReadManifest(source)) != manifest)
                throw new IOException("Service build output changed during deployment. Finish the build and retry; no partial snapshot was published.");
            try
            {
                // Same-volume directory rename publishes the complete snapshot, never individual files.
                Directory.Move(staging, destination);
            }
            catch (IOException) when (Directory.Exists(destination))
            {
                RequireMatchingSnapshot(destination, manifest);
            }
            return Path.Combine(destination, name);
        }
        finally
        {
            // Only this attempt's unpublished staging directory is eligible for cleanup.
            // Published directories may contain running executables and are never modified here.
            try
            {
                if (Directory.Exists(staging))
                {
                    if (OperatingSystem.IsWindows())
                        foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException) { Trace.TraceWarning("Could not remove unpublished service staging directory: {0}", staging); }
            catch (UnauthorizedAccessException) { Trace.TraceWarning("Could not remove unpublished service staging directory: {0}", staging); }
        }
    }

    private static void RequireMatchingSnapshot(string directory, string manifest)
    {
        if (ManifestText(ReadManifest(directory)) != manifest)
            throw new IOException("The service deployment contents do not match their source manifest. Refusing to overwrite or launch the snapshot.");
    }

    private static string ManifestText(IReadOnlyList<DeploymentFile> files) =>
        string.Join("\n", files.Select(file => $"{file.Path.Replace('\\', '/')}\0{file.Mode}\0{file.Hash}"));

    private static List<DeploymentFile> ReadManifest(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Service runtime directories cannot be symbolic links or junctions.");
        var files = new List<DeploymentFile>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Service runtime assets cannot be symbolic links or junctions; publish regular files before deployment.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                var mode = OperatingSystem.IsWindows() ? 0 : (int)(File.GetUnixFileMode(entry)
                    & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
                files.Add(new DeploymentFile(Path.GetRelativePath(directory, entry),
                    Convert.ToHexStringLower(SHA256.HashData(stream)), mode));
            }
        }
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path.Replace('\\', '/'), right.Path.Replace('\\', '/')));
        return files;
    }

    private sealed record DeploymentFile(string Path, string Hash, int Mode);
}
