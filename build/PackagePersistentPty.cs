using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// Explicit build-time packaging only. Never loads or executes the downloaded native code.
public sealed class PackagePersistentPty : Microsoft.Build.Utilities.Task
{
    [Required] public string Pins { get; set; }
    [Required] public string RuntimeIdentifier { get; set; }
    [Required] public string OutputDirectory { get; set; }
    public string Archive { get; set; }
    [Output] public ITaskItem[] Files { get; set; }

    public override bool Execute()
    {
        try
        {
            using var pins = JsonDocument.Parse(File.ReadAllText(Pins));
            var root = pins.RootElement;
            if (!root.GetProperty("targets").TryGetProperty(RuntimeIdentifier, out var target))
            {
                Files = Array.Empty<ITaskItem>();
                Log.LogMessage(MessageImportance.High, "No upstream opencode-pty {0} artifact exists for {1}; persistent PTY packaging is unavailable for this target.", root.GetProperty("version").GetString(), RuntimeIdentifier);
                return true;
            }
            byte[] archive;
            if (!string.IsNullOrEmpty(Archive)) archive = File.ReadAllBytes(Archive);
            else
            {
                using var http = new HttpClient();
                archive = http.GetByteArrayAsync(target.GetProperty("tarball").GetString()).GetAwaiter().GetResult();
            }
            var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(archive));
            if (integrity != target.GetProperty("integrity").GetString()) throw new InvalidDataException("Pinned opencode-pty archive integrity mismatch.");
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var bytes = new MemoryStream(archive, false))
            using (var gzip = new GZipStream(bytes, CompressionMode.Decompress))
            using (var tar = new TarReader(gzip))
            {
                TarEntry entry;
                while ((entry = tar.GetNextEntry()) != null)
                {
                    if (entry.EntryType != TarEntryType.RegularFile && entry.EntryType != TarEntryType.V7RegularFile)
                        throw new InvalidDataException("The pinned native package must contain regular files only.");
                    if (entry.Name != "package/bin/opencode-pty" && entry.Name != "package/LICENSE" && entry.Name != "package/package.json")
                        throw new InvalidDataException("Unexpected pinned native package member: " + entry.Name);
                    using var data = new MemoryStream();
                    entry.DataStream.CopyTo(data);
                    contents.Add(entry.Name, data.ToArray());
                }
            }
            if (contents.Count != 3) throw new InvalidDataException("The native package is incomplete.");
            Check(contents["package/bin/opencode-pty"], target.GetProperty("sha256").GetString());
            Check(contents["package/LICENSE"], root.GetProperty("licenseSha256").GetString());
            Check(contents["package/package.json"], target.GetProperty("packageSha256").GetString());
            using (var manifest = JsonDocument.Parse(contents["package/package.json"]))
            {
                if (manifest.RootElement.GetProperty("name").GetString() != target.GetProperty("package").GetString()
                    || manifest.RootElement.GetProperty("version").GetString() != root.GetProperty("version").GetString()
                    || manifest.RootElement.GetProperty("license").GetString() != root.GetProperty("license").GetString())
                    throw new InvalidDataException("Native package identity does not match its pins.");
            }
            var destination = Path.GetFullPath(OutputDirectory);
            Directory.CreateDirectory(destination);
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native staging directory cannot be a link.");
            var files = new List<ITaskItem>();
            foreach (var item in contents)
            {
                var name = item.Key == "package/bin/opencode-pty" ? "opencode-pty" : Path.GetFileName(item.Key);
                var file = Path.Combine(destination, name);
                if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Native staging files cannot be links.");
                File.WriteAllBytes(file, item.Value);
                if (!OperatingSystem.IsWindows() && name == "opencode-pty")
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                var output = new TaskItem(file);
                output.SetMetadata("TargetPath", "native/opencode-pty/" + RuntimeIdentifier + "/" + name);
                output.SetMetadata("CopyToOutputDirectory", "PreserveNewest");
                output.SetMetadata("CopyToPublishDirectory", "PreserveNewest");
                files.Add(output);
            }
            Files = files.ToArray();
            Log.LogMessage(MessageImportance.High, "Packaged {0}@{1}; SHA-512 archive and SHA-256 executable/license/package metadata verified. No native code executed.",
                target.GetProperty("package").GetString(), root.GetProperty("version").GetString());
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, false); return false; }
    }

    private static void Check(byte[] bytes, string expected)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != expected)
            throw new InvalidDataException("Pinned native package content hash mismatch.");
    }
}
