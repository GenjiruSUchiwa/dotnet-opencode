using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Build.Framework;

// MSBuild-only source fingerprinting. Never loads the application or reads runtime state.
public sealed class ComputeApplicationBuild : Microsoft.Build.Utilities.Task
{
    [Required] public string RepositoryRoot { get; set; }
    [Required] public string Flavor { get; set; }
    [Required] public string OutputDirectory { get; set; }
    public string Expected { get; set; }
    public string StampFile { get; set; }
    public bool LocalBuild { get; set; }
    public string BuildTimestamp { get; set; }
    public bool FingerprintOnly { get; set; }
    [Output] public string Fingerprint { get; set; }

    public override bool Execute()
    {
        try
        {
            var root = Path.GetFullPath(RepositoryRoot);
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(Path.Combine(root, "src"));
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".csproj", ".razor", ".props", ".targets", ".json", ".resx", ".resources",
                ".dll", ".exe", ".so", ".dylib", ".wasm", ".css", ".js", ".ts", ".ttf", ".otf",
                ".png", ".svg", ".txt", ".md", ".xml", ".config"
            };
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name == ".git" || name == ".dotnet") continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Application build inputs must not contain linked directories.");
                    pending.Push(child);
                }
                files.AddRange(Directory.EnumerateFiles(directory).Where(file => extensions.Contains(Path.GetExtension(file))));
            }
            foreach (var name in new[] { "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "nuget.config", "run.ps1", "build/ComputeApplicationBuild.cs", "build/PackagePersistentPty.cs", "build/PersistentPty.targets", "build/opencode-pty.assets.json", "build/OpenCode.Build/OpenCode.Build.csproj" })
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path)) files.Add(path);
            }
            var manifest = new StringBuilder("opencode-source-build-v1\n").Append(Flavor).Append('\n');
            using (var sha = SHA256.Create())
            {
                foreach (var file in files.Distinct(StringComparer.Ordinal).OrderBy(file => Relative(root, file), StringComparer.Ordinal))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Application build inputs must not contain linked files.");
                    using (var stream = File.OpenRead(file))
                        manifest.Append(Relative(root, file)).Append('\0').Append(Hex(sha.ComputeHash(stream))).Append('\n');
                }
                Fingerprint = Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())));
            }
            Directory.CreateDirectory(OutputDirectory);
            WriteIfChanged(Path.Combine(OutputDirectory, "ApplicationBuild.inputs." + Fingerprint + ".txt"), manifest.ToString());
            if (!string.IsNullOrEmpty(Expected) && Expected != Fingerprint)
                throw new InvalidOperationException("Application inputs changed during this build. Build again from a stable source tree; no older executable should be launched.");
            if (!FingerprintOnly)
            {
                long timestamp = 0;
                if (LocalBuild && (!long.TryParse(BuildTimestamp, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp) || timestamp <= 0))
                    throw new InvalidOperationException("A dotnet-local build requires its stable OpenCodeBuildTimestamp. Use run.ps1.");
                var version = LocalBuild
                    ? "\"0.1.0-dotnet-local." + DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "\""
                    : "OpenCode.Schema.OpenCodeChannel.ServiceVersion";
                WriteIfChanged(Path.Combine(OutputDirectory, "ApplicationBuild.g.cs"),
                    "// Generated source identity; local timestamp is reused for unchanged builds.\n" +
                    "namespace OpenCode.Protocol;\npublic static class ApplicationBuild { public const string Id = \"" + Fingerprint + "\"; " +
                    "public const long Timestamp = " + timestamp.ToString(CultureInfo.InvariantCulture) + "; public const string Version = " + version + "; }\n");
                WriteIfChanged(Path.Combine(OutputDirectory, "opencode-build.timestamp"), timestamp.ToString(CultureInfo.InvariantCulture) + "\n");
            }
            WriteIfChanged(Path.Combine(OutputDirectory, "opencode-build.id"), Fingerprint + "\n");
            if (!string.IsNullOrEmpty(StampFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StampFile)));
                WriteIfChanged(StampFile, Fingerprint + "\n");
            }
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, false); return false; }
    }

    private static string Relative(string root, string file) => file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
    private static string Hex(byte[] value) => BitConverter.ToString(value).Replace("-", "").ToLowerInvariant();
    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
