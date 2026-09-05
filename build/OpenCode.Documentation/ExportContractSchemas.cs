using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using OpenCode.Protocol.Documentation;
using OpenCode.Schema;
using OpenCode.Server.Documentation;

/// <summary>Pure Schema/Protocol metadata generation; this build tool does not reference Server/Core or invoke an application.</summary>
public sealed class ExportContractSchemas : Microsoft.Build.Utilities.Task
{
    [Required] public string OutputFile { get; set; } = "";
    public override bool Execute()
    {
        var source = CanonicalOpenApi.Read();
        var components = source.Document["components"]!["schemas"]!.DeepClone().AsObject();
        var exporter = new NativeSchemaExporter(components);
        var roots = new JsonObject();
        var unmapped = new JsonArray();
        foreach (var type in new[] { typeof(SessionInfo), typeof(SessionMessage), typeof(SessionInboxItem), typeof(ModelInfo), typeof(ProviderInfo),
            typeof(AgentInfo), typeof(FormInfo), typeof(FormReply), typeof(McpServer), typeof(McpServerConfig), typeof(IntegrationInfo),
            typeof(IntegrationAttemptStatus), typeof(PermissionRequest), typeof(ShellInfo), typeof(ShellOutput), typeof(PersistentPtyInfo),
            typeof(PersistentPtySnapshot), typeof(ProjectInfo), typeof(WorktreeCreateInput), typeof(ConfigEntry), typeof(OpenCodeEvent),
            typeof(SessionTransferData), typeof(SessionStatsInfo), typeof(ReferenceInfo), typeof(SkillInfo), typeof(FileSystemEntry),
            typeof(WorktreeDirectory), typeof(VcsBase), typeof(PermissionSavedInfo), typeof(PersistentPtyCreateInput), typeof(PtyCreateInput) })
        {
            try { roots[type.FullName!] = exporter.Reference(type); }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or JsonException)
            { unmapped.Add(new JsonObject { ["type"] = type.FullName, ["reason"] = error.Message, ["metadataTrace"] = error.StackTrace }); }
        }
        var referenceErrors = SchemaReferences.Check(components, roots);
        var output = new JsonObject { ["sourceSha256"] = source.Sha256, ["roots"] = roots, ["schemas"] = components, ["unmapped"] = unmapped,
            ["unresolvedReferences"] = new JsonArray(referenceErrors.Select(error => (JsonNode?)JsonValue.Create(error)).ToArray()) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile))!);
        File.WriteAllText(OutputFile, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log.LogMessage(MessageImportance.High, "Exported {0} Schema/Protocol metadata roots; {1} unmapped; {2} unresolved references. No Server/Core/application reference or handler execution.", roots.Count, unmapped.Count, referenceErrors.Count);
        return true;
    }
}
