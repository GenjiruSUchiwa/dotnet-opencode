namespace OpenCode.Server.Endpoints;

using OpenCode.Core.Database;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class ProviderEndpointsMapping
{
    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/provider", async (CredentialStore credentialStore, CancellationToken ct) =>
        {
            var creds = await credentialStore.ListCredentialsAsync(ct);
            var providers = creds.Select(c => new ProviderInfo(
                Id: c.IntegrationId,
                Name: c.Label,
                Activation: c.Active ? ProviderActivation.Enabled : ProviderActivation.Auto,
                Package: ""
            )).ToArray();

            return Results.Ok(new LocationResponse<IReadOnlyList<ProviderInfo>>(
                Location: null,
                Data: providers
            ));
        });
    }
}
