using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// MSBuild file staging only. No application/assembly loading or service operation.
public sealed class StageDotnetOpencodeServer : Task
{
    [Required] public string ServerAssembly { get; set; }
    [Required] public string CliDirectory { get; set; }
    [Output] public ITaskItem[] Files { get; set; }

    public override bool Execute()
    {
        var source = Path.GetDirectoryName(Path.GetFullPath(ServerAssembly));
        var cli = Path.GetFullPath(CliDirectory);
        foreach (var name in new[] { "OpenCode.Server.dll", "OpenCode.Server.deps.json", "OpenCode.Server.runtimeconfig.json", "opencode-build.id" })
            if (!File.Exists(Path.Combine(source, name))) throw new InvalidOperationException("Missing built Server runtime asset: " + name);
        var stamp = File.ReadAllText(Path.Combine(source, "opencode-build.id")).Trim();
        if (stamp.Length != 64 || stamp != File.ReadAllText(Path.Combine(cli, "opencode-build.id")).Trim())
            throw new InvalidOperationException("CLI and Server build identities differ. No mixed tool package may be staged.");
        var output = Path.Combine(cli, "server");
        Directory.CreateDirectory(output);
        var result = new List<ITaskItem>();
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(file => file, StringComparer.Ordinal))
        {
            var relative = file.Substring(source.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Server runtime assets must be regular files: " + relative);
            var destination = Path.Combine(output, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(file, destination, true);
            var item = new TaskItem(destination);
            item.SetMetadata("ToolRelativePath", relative.Replace('\\', '/'));
            result.Add(item);
        }
        Files = result.ToArray();
        Log.LogMessage(MessageImportance.High, "Staged complete Server runtime ({0} files) under {1}", Files.Length, output);
        return true;
    }
}
