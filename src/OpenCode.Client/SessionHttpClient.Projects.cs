namespace OpenCode.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task<ProjectInfo> UpdateProjectAsync(ProjectId id, string? name = null, ProjectIcon? icon = null,
        ProjectCommands? commands = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id.Value);
        return RequestAsync(HttpMethod.Patch, "/api/project/" + Uri.EscapeDataString(id.Value), ProjectHttpJsonContext.Default.ProjectInfo, ct,
            JsonContent.Create(new ProjectUpdateHttpInput(name, icon, commands), ProjectHttpJsonContext.Default.ProjectUpdateHttpInput));
    }

    public Task<IReadOnlyList<ProjectInfo>> ListProjectsAsync(CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/project", ProjectHttpJsonContext.Default.Projects, ct);
    public Task<LocationProjectInfo> CurrentProjectAsync(string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/project/current" + Query(("location[directory]", directory), ("location[workspace]", workspace)),
            ProjectHttpJsonContext.Default.LocationProjectInfo, ct);
}

internal sealed record ProjectUpdateHttpInput(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("icon")] ProjectIcon? Icon,
    [property: JsonPropertyName("commands")] ProjectCommands? Commands);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(IReadOnlyList<ProjectInfo>), TypeInfoPropertyName = "Projects")]
[JsonSerializable(typeof(LocationProjectInfo))]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(ProjectUpdateHttpInput))]
internal partial class ProjectHttpJsonContext : JsonSerializerContext;
