namespace OpenCode.Client;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Protocol;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    public Task<LocationResponse<VcsBase?>> VcsBaseAsync(string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/vcs/base" + Query(("location[directory]", directory), ("location[workspace]", workspace)),
            VcsProtocolJsonContext.Default.BaseResult, ct);

    public Task<LocationResponse<IReadOnlyList<FileDiffInfo>>> VcsDiffAsync(VcsDiffMode mode, string? baseRef = null, int? context = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        VcsDiffAsync(VcsDiffModeJsonConverter.Encode(mode), baseRef, context, directory, workspace, ct);

    public Task<LocationResponse<VcsInfo>> GetVcsAsync(string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/vcs" + Query(("location[directory]", directory), ("location[workspace]", workspace)), VcsHttpJsonContext.Default.Info, ct);
    public Task<LocationResponse<IReadOnlyList<VcsFileStatus>>> VcsStatusAsync(string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequestAsync(HttpMethod.Get, "/api/vcs/status" + Query(("location[directory]", directory), ("location[workspace]", workspace)), VcsHttpJsonContext.Default.Status, ct);
    public Task<LocationResponse<IReadOnlyList<string>>> VcsBranchesAsync(string? search = null, int? limit = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        if (limit is < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        return RequestAsync(HttpMethod.Get, "/api/vcs/branches" + Query(("location[directory]", directory), ("location[workspace]", workspace),
            ("search", search), ("limit", limit?.ToString(CultureInfo.InvariantCulture))), VcsHttpJsonContext.Default.Branches, ct);
    }
    public Task<LocationResponse<IReadOnlyList<FileDiffInfo>>> VcsDiffAsync(string mode, string? baseRef = null, int? context = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        if (mode is not ("working" or "branch" or "committed")) throw new ArgumentException("VCS mode must be working, branch, or committed.", nameof(mode));
        if (context is < 0) throw new ArgumentOutOfRangeException(nameof(context));
        return RequestAsync(HttpMethod.Get, "/api/vcs/diff" + Query(("location[directory]", directory), ("location[workspace]", workspace),
            ("mode", mode), ("base", baseRef), ("context", context?.ToString(CultureInfo.InvariantCulture))), VcsHttpJsonContext.Default.Diff, ct);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocationResponse<VcsInfo>), TypeInfoPropertyName = "Info")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<VcsFileStatus>>), TypeInfoPropertyName = "Status")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<string>>), TypeInfoPropertyName = "Branches")]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<FileDiffInfo>>), TypeInfoPropertyName = "Diff")]
internal partial class VcsHttpJsonContext : JsonSerializerContext;
