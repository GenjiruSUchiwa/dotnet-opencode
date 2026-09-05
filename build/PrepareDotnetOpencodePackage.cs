using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Static build/package inspection. Never loads an application, native DLL or WASM module.
public sealed class PrepareDotnetOpencodePackage : Task
{
    [Required] public string RepositoryRoot { get; set; }
    [Required] public string PublishDirectory { get; set; }
    [Required] public string AssetsFile { get; set; }
    [Required] public string SdkVersion { get; set; }
    public string NativeSourceDirectory { get; set; }
    public bool ImportNativeOnly { get; set; }
    private const string NativeHash = "294550aa81a50439e42ecb73df97b99d6ec3dc2ecb1f3f18bb453076140e1f0e";

    public override bool Execute()
    {
        var root = Path.GetFullPath(RepositoryRoot);
        var output = Path.GetFullPath(PublishDirectory);
        var requiredSdk = (string)JObject.Parse(File.ReadAllText(Path.Combine(root, "global.json")))["sdk"]["version"];
        if (SdkVersion != requiredSdk) throw new InvalidOperationException("Package with the exact repository-local SDK pin: " + requiredSdk);
        if (ImportNativeOnly)
        {
            if (string.IsNullOrEmpty(NativeSourceDirectory)) throw new InvalidOperationException("Native notice import requires an explicit reviewed source directory.");
            var input = Require(root, "src/OpenTui.Native/runtimes/win-x64/native/opentui.dll");
            HashMatches(input, NativeHash);
            ImportNativeNotices(input, Path.Combine(root, "packaging", "third-party", "opentui-0.5.9"));
            Log.LogMessage(MessageImportance.High, "Imported verified OpenTUI native component notices into packaging/third-party.");
            return true;
        }
        foreach (var name in new[] { "OpenCode.Cli.dll", "OpenCode.Cli.deps.json", "OpenCode.Cli.runtimeconfig.json", "opencode-build.id",
            "server/OpenCode.Server.dll", "server/OpenCode.Server.deps.json", "server/OpenCode.Server.runtimeconfig.json", "server/opencode-build.id" })
            Require(output, name);
        if (File.ReadAllText(Path.Combine(output, "opencode-build.id")).Trim() != File.ReadAllText(Path.Combine(output, "server/opencode-build.id")).Trim())
            throw new InvalidOperationException("Published CLI/Server identities do not match.");

        var native = Require(output, "runtimes/win-x64/native/opentui.dll");
        HashMatches(native, NativeHash);
        var notices = Path.Combine(root, "packaging", "third-party", "opentui-0.5.9");
        var provenance = JObject.Parse(File.ReadAllText(Require(notices, "provenance.json")));
        foreach (var item in (JArray)provenance["files"])
            HashMatches(Require(notices, (string)item["file"]), (string)item["sha256"]);
        CopyTree(notices, Path.Combine(output, "third-party", "opentui-0.5.9"));
        CopyTree(Path.Combine(root, "src", "OpenTui.Blazor", "Code", "Assets"), Path.Combine(output, "third-party", "tree-sitter-assets"), true);
        File.Copy(Require(root, "packaging/THIRD-PARTY-NOTICES.md"), Path.Combine(output, "THIRD-PARTY-NOTICES.md"), true);
        File.Copy(Require(root, "src/OpenCode.Cli/Tui/Theme/Assets/catalog-provenance.json"), Path.Combine(output, "third-party", "theme-catalog-provenance.json"), true);
        File.Copy(Require(root, "src/OpenCode.Cli/Commands/Run/RunMimeRegistry.Generated.cs"), Path.Combine(output, "third-party", "mime-registry-provenance.cs.txt"), true);
        File.Copy(Require(root, "src/OpenCode.Cli/Tui/Dialogs/LICENSE.fuzzysort"), Path.Combine(output, "third-party", "LICENSE.fuzzysort"), true);

        var grammarRoot = Path.Combine(output, "Tui", "Transcript", "GrammarAssets");
        var grammar = JObject.Parse(File.ReadAllText(Require(grammarRoot, "manifest.json")));
        var grammarAssets = 0;
        foreach (var entry in (JArray)grammar["entries"])
        {
            var entryAssets = new List<JToken> { entry["wasm"] };
            foreach (var query in (JArray)entry["highlights"]) entryAssets.Add(query);
            foreach (var asset in entryAssets)
            {
                var content = Require(grammarRoot, "content/" + (string)asset["sha256"]);
                HashMatches(content, (string)asset["sha256"]);
                if (new FileInfo(content).Length != (long)asset["bytes"]) throw new InvalidDataException("Grammar byte count mismatch.");
                HashMatches(Require(grammarRoot, "licenses/" + (string)asset["licenseFile"]), (string)asset["licenseSha256"]);
                grammarAssets++;
            }
        }
        var packages = PackageNotices(output);
        var inventory = new JObject
        {
            ["format"] = 1, ["packageId"] = "dotnet-opencode", ["command"] = "dotnet opencode",
            ["sdk"] = SdkVersion, ["verification"] = "Static build asset hashes and manifests only; no application/native/WASM execution.",
            ["opentui"] = provenance, ["grammarAssetCount"] = grammarAssets, ["dependencies"] = packages,
            ["files"] = new JArray(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file) != "tool-runtime-assets.json"
                    && Relative(output, file) != "OpenCode.Cli.exe" && Relative(output, file) != "OpenCode.Cli")
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(file => new JObject { ["path"] = Relative(output, file), ["bytes"] = new FileInfo(file).Length, ["sha256"] = Hash(file) }))
        };
        File.WriteAllText(Path.Combine(output, "tool-runtime-assets.json"), inventory.ToString(Formatting.Indented), new UTF8Encoding(false));
        Log.LogMessage(MessageImportance.High, "Verified tool payload: {0} grammar assets, {1} NuGet dependency records; complete Server and pinned OpenTUI included.", grammarAssets, packages.Count);
        return true;
    }

    private void ImportNativeNotices(string native, string destination)
    {
        var package = JObject.Parse(File.ReadAllText(Require(NativeSourceDirectory, "package.json")));
        if ((string)package["name"] != "@opentui/core-win32-x64" || (string)package["version"] != "0.5.9")
            throw new InvalidOperationException("OpenTUI provenance input must be the reviewed @opentui/core-win32-x64 0.5.9 package.");
        HashMatches(Require(NativeSourceDirectory, "opentui.dll"), Hash(native));
        Directory.CreateDirectory(destination);
        var files = new JArray();
        foreach (var name in new[] { "package.json", "LICENSE", "LICENSE-GHOSTTY", "LICENSE-LCMS2", "LICENSE-LIBWEBP", "LICENSE-STB", "LICENSE-WUFFS", "AUTHORS-LIBWEBP", "PATENTS-LIBWEBP" })
        {
            var source = Require(NativeSourceDirectory, name);
            File.Copy(source, Path.Combine(destination, name), true);
            files.Add(new JObject { ["file"] = name, ["sha256"] = Hash(source) });
        }
        var manifest = new JObject { ["package"] = "@opentui/core-win32-x64", ["version"] = "0.5.9", ["runtime"] = "win-x64",
            ["binary"] = "opentui.dll", ["sha256"] = NativeHash,
            ["source"] = "https://registry.npmjs.org/@opentui/core-win32-x64/-/core-win32-x64-0.5.9.tgz",
            ["verification"] = "Bundled DLL SHA-256 equals installed upstream package DLL; no loading or execution.", ["files"] = files };
        File.WriteAllText(Path.Combine(destination, "provenance.json"), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
    }

    private JArray PackageNotices(string output)
    {
        var assets = JObject.Parse(File.ReadAllText(AssetsFile));
        var folders = ((JObject)assets["packageFolders"]).Properties().Select(property => property.Name).ToArray();
        var result = new JArray();
        foreach (var library in ((JObject)assets["libraries"]).Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if ((string)library.Value["type"] != "package") continue;
            var folder = folders.Select(path => Path.Combine(path, (string)library.Value["path"])).First(Directory.Exists);
            // Tool stores repeat package ID/version in their installation path.
            // Keep notice paths short; the manifest retains full dependency identity.
            var destination = Path.Combine(output, "third-party", "nuget", "p" + result.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            var records = new JArray();
            foreach (var file in ((JArray)library.Value["files"]).Values<string>())
            {
                var name = Path.GetFileName(file);
                if (!file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(name, "^(license|licence|notice|third.?party|copying|authors|patents)", RegexOptions.IgnoreCase)) continue;
                var source = Require(folder, file);
                // NuGet excludes nested .nuspec files when packing. Preserve their
                // unchanged XML bytes under a non-reserved extension instead.
                var packagedName = file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ? "metadata.xml" : file;
                var target = Path.Combine(destination, packagedName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
                records.Add(new JObject { ["path"] = Relative(output, target), ["sourceFile"] = file, ["sha256"] = Hash(source) });
            }
            result.Add(new JObject { ["package"] = library.Name, ["sha512"] = (string)library.Value["sha512"], ["notices"] = records });
        }
        return result;
    }
    private static void CopyTree(string source, string target, bool noticesOnly = false)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (noticesOnly && !Path.GetFileName(file).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(file) != "NOTICE.md") continue;
            var destination = Path.Combine(target, Relative(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
        }
    }
    private static string Require(string root, string relative)
    {
        var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file)) throw new FileNotFoundException("Missing tool package input: " + relative, file);
        return file;
    }
    private static string Relative(string root, string path) => path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace('\\', '/');
    private static string Hash(string file) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void HashMatches(string file, string expected) { if (Hash(file) != expected) throw new InvalidDataException("Package asset digest mismatch: " + file); }
}
