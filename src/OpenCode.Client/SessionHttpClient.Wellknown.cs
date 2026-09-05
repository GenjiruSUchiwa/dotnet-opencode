namespace OpenCode.Client;

using System.Net.Http.Json;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>The source's single experimental discovery operation. HTTP success means source registration, not login.</summary>
    public Task AddWellknownIntegrationAsync(IntegrationWellknownAddPayload input, LocationRef? location = null, CancellationToken ct = default) =>
        NoContentAsync(HttpMethod.Post, "/api/experimental/integration/wellknown"
            + IntegrationQuery(location?.Directory, location?.WorkspaceId), ct,
            JsonContent.Create(input, OpenCodeJsonContext.Default.IntegrationWellknownAddPayload));
}
