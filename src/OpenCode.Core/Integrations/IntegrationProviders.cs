namespace OpenCode.Core.Integrations;

using System.Text.Json;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Mcp;
using OpenCode.Schema;

public sealed record IntegrationOAuthCallback(string Code, string State, string? Issuer);
public sealed record IntegrationCallbackOptions(Uri? RedirectUri = null, int? Port = null,
    string Host = "127.0.0.1", string Path = "/callback");

public interface IIntegrationCallbackListener : IAsyncDisposable
{
    Uri RedirectUri { get; }
    void ExpectAuthorization(Uri authorizationUri);
    Task<IntegrationOAuthCallback> WaitAsync(CancellationToken ct);
}

public interface IIntegrationCallbackFactory
{
    Task<IIntegrationCallbackListener> BindAsync(IntegrationCallbackOptions options, CancellationToken ct);
}

/// <summary>Only source-backed, implemented methods. No plugin/command/wellknown placeholder registrations.</summary>
public sealed class IntegrationProviders(CredentialStore credentials, ConsoleIntegrationService console, OpenAiOAuthService openAi,
    IIntegrationCallbackFactory callbacks, Func<CredentialMutation, CancellationToken, Task> publish)
{
    public async Task<IReadOnlyList<IntegrationDefinition>> DefinitionsAsync(McpRuntime mcp, CancellationToken ct)
    {
        var result = new List<IntegrationDefinition>
        {
            new(new(IntegrationId.FromExisting("opencode"), "OpenCode"),
                [new IntegrationOAuthMethod(ConsoleIntegrationService.MethodId, "OpenCode Console account"),
                    new IntegrationKeyMethod { Label = "API key (service account)" }],
                new Dictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>>
                { [ConsoleIntegrationService.MethodId] = ConsoleAsync }),
            new(new(IntegrationId.FromExisting("openai"), "OpenAI"),
                [new IntegrationKeyMethod(), new IntegrationOAuthMethod(OpenAiOAuthService.BrowserMethodId, "ChatGPT Pro/Plus (browser)"),
                    new IntegrationOAuthMethod(OpenAiOAuthService.HeadlessMethodId, "ChatGPT Pro/Plus (headless)")],
                new Dictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>>
                { [OpenAiOAuthService.BrowserMethodId] = BrowserAsync, [OpenAiOAuthService.HeadlessMethodId] = HeadlessAsync })
        };
        foreach (var registration in await mcp.OAuthRegistrationsAsync(ct).ConfigureAwait(true))
        {
            result.Add(new(new(IntegrationId.FromExisting(registration.IntegrationId), registration.Server,
                new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("mcp", OpenCodeJsonContext.Default.String) }),
                [new IntegrationOAuthMethod(registration.IntegrationId, registration.Server)],
                new Dictionary<string, Func<FormAnswer?, string?, CancellationToken, Task<IntegrationAuthorization>>>
                { [registration.IntegrationId] = (_, label, token) => McpAsync(mcp, registration, label, token) }));
        }
        return result;
    }

    private async Task<IntegrationAuthorization> ConsoleAsync(FormAnswer? answer, string? label, CancellationToken ct)
    {
        var server = answer?.GetValueOrDefault("server") switch
        {
            null => null,
            FormValue.Text text => text.Value,
            _ => throw new IntegrationAuthorizationException("OpenCode server must be a string.")
        };
        var attempt = await console.BeginDeviceAuthorizationAsync(server, ct).ConfigureAwait(true);
        return new(attempt.VerificationUri.AbsoluteUri, $"Enter code: {attempt.UserCode}", "auto", attempt.ExpiresAt,
            async (_, token) => await publish(await console.CompleteDeviceAuthorizationAsync(attempt, label, token).ConfigureAwait(true), CancellationToken.None).ConfigureAwait(true),
            () => ValueTask.CompletedTask);
    }

    private async Task<IntegrationAuthorization> HeadlessAsync(FormAnswer? answer, string? label, CancellationToken ct)
    {
        var attempt = await openAi.BeginHeadlessAuthorizationAsync(ct).ConfigureAwait(true);
        return new(attempt.VerificationUri.AbsoluteUri, $"Enter code: {attempt.UserCode}", "auto", attempt.ExpiresAt,
            async (_, token) => await publish(await openAi.CompleteHeadlessAuthorizationAsync(attempt, label, token).ConfigureAwait(true), CancellationToken.None).ConfigureAwait(true),
            () => ValueTask.CompletedTask);
    }

    private async Task<IntegrationAuthorization> BrowserAsync(FormAnswer? answer, string? label, CancellationToken ct)
    {
        IIntegrationCallbackListener listener;
        try { listener = await callbacks.BindAsync(new(Port: 1455, Host: "localhost", Path: "/auth/callback"), ct).ConfigureAwait(true); }
        catch (IntegrationCallbackAddressInUseException)
        { listener = await callbacks.BindAsync(new(Port: 1457, Host: "localhost", Path: "/auth/callback"), ct).ConfigureAwait(true); }
        try
        {
            var attempt = openAi.BeginBrowserAuthorization(listener.RedirectUri.Port);
            listener.ExpectAuthorization(attempt.AuthorizationUri);
            return new(attempt.AuthorizationUri.AbsoluteUri, "Complete authorization in your browser. This window will close automatically.", "auto", attempt.ExpiresAt,
                async (_, token) =>
                {
                    var callback = await listener.WaitAsync(token).ConfigureAwait(true);
                    await publish(await openAi.CompleteBrowserAuthorizationAsync(attempt, callback.Code, callback.State, label, token).ConfigureAwait(true), CancellationToken.None).ConfigureAwait(true);
                }, listener.DisposeAsync);
        }
        catch { await listener.DisposeAsync().ConfigureAwait(true); throw; }
    }

    private async Task<IntegrationAuthorization> McpAsync(McpRuntime mcp, McpOAuthRegistration registration, string? label, CancellationToken ct)
    {
        var listener = await callbacks.BindAsync(new(registration.RedirectUri, registration.CallbackPort), ct).ConfigureAwait(true);
        McpOAuthAuthorization? attempt = null;
        try
        {
            // The actual port is bound before SDK discovery/DCR builds the redirect URL.
            attempt = await mcp.StartAuthorizationAsync(registration.Server, listener.RedirectUri, ct).ConfigureAwait(true);
            var started = attempt;
            listener.ExpectAuthorization(started.AuthorizationUri);
            return new(started.AuthorizationUri.AbsoluteUri, $"Authorize {registration.Server} in your browser. This window will close automatically.", "auto", started.ExpiresAt,
                async (_, token) =>
                {
                    var callback = await listener.WaitAsync(token).ConfigureAwait(true);
                    var selectedLabel = label ?? await IntegrationRuntime.UniqueLabelAsync(credentials, registration.IntegrationId, registration.Server, token).ConfigureAwait(true);
                    await mcp.CompleteAuthorizationAsync(started, callback.Code, callback.State, callback.Issuer, selectedLabel, token).ConfigureAwait(true);
                }, async () =>
                {
                    try { await started.DisposeAsync().ConfigureAwait(true); }
                    finally { await listener.DisposeAsync().ConfigureAwait(true); }
                });
        }
        catch
        {
            try { if (attempt is not null) await attempt.DisposeAsync().ConfigureAwait(true); }
            finally { await listener.DisposeAsync().ConfigureAwait(true); }
            throw;
        }
    }
}

public sealed class IntegrationCallbackAddressInUseException() : IOException("The OAuth callback port is already in use.");
