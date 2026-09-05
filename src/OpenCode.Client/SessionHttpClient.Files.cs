namespace OpenCode.Client;

using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Direct children relative to the resolved server Location. No local filesystem lookup.</summary>
    public async Task<LocationResponse<IReadOnlyList<FileSystemEntry>>> ListFilesAsync(string? path = null,
        string? directory = null, string? workspace = null, CancellationToken ct = default) =>
        RequireFileEntries(await RequestAsync(HttpMethod.Get, FsEndpoints.List + Query(("path", path), ("location[directory]", directory),
            ("location[workspace]", workspace)), FilesHttpJsonContext.Default.Entries, ct).ConfigureAwait(false));

    /// <summary>Returns the server's recursive ranking; entries are not reranked or checked against client files.</summary>
    public async Task<LocationResponse<IReadOnlyList<FileSystemEntry>>> FindFilesAsync(FileSystemFindInput input,
        string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Query);
        if (input.Limit is <= 0) throw new ArgumentOutOfRangeException(nameof(input), "Limit must be a positive integer.");
        var type = input.Type switch
        {
            null => null, FileSystemEntryType.File => "file", FileSystemEntryType.Directory => "directory",
            _ => throw new ArgumentOutOfRangeException(nameof(input), "Unsupported filesystem entry type.")
        };
        return RequireFileEntries(await RequestAsync(HttpMethod.Get, FsEndpoints.Find + Query(("query", input.Query), ("type", type),
            ("limit", input.Limit?.ToString(CultureInfo.InvariantCulture)), ("location[directory]", directory),
            ("location[workspace]", workspace)), FilesHttpJsonContext.Default.Entries, ct).ConfigureAwait(false));
    }

    /// <summary>Canonical fs.read is raw bytes, not a JSON text/content envelope.</summary>
    public async Task<byte[]> ReadFileAsync(string path, string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path is "." or "..") throw new ArgumentException("A file path is required.", nameof(path));
        var route = FsEndpoints.Read.Replace("{*path}", Uri.EscapeDataString(path.Replace('\\', '/')), StringComparison.Ordinal)
            + Query(("location[directory]", directory), ("location[workspace]", workspace));
        using var request = CreateRequest(HttpMethod.Get, route, "*/*");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
        await RequireSuccessAsync(response, route, cancellation.Token, HttpStatusCode.OK).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellation.Token).ConfigureAwait(false);
    }

    private static LocationResponse<IReadOnlyList<FileSystemEntry>> RequireFileEntries(LocationResponse<IReadOnlyList<FileSystemEntry>> response)
    {
        if (response.Data is null || response.Data.Any(entry => entry is null || entry.Path is null
            || entry.Type is not (FileSystemEntryType.File or FileSystemEntryType.Directory)))
            throw Malformed("fs", "Filesystem response requires canonical entries.");
        return response;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(LocationResponse<IReadOnlyList<FileSystemEntry>>), TypeInfoPropertyName = "Entries")]
internal partial class FilesHttpJsonContext : JsonSerializerContext;
