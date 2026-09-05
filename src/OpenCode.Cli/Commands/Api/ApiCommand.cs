namespace OpenCode.Cli.Commands.Api;

using System.Text;
using System.Text.Json;
using OpenCode.Cli.Hosting;
using OpenCode.Client;
using Transport;

public static class ApiCommand
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "ArgumentException.Message is the existing CLI output contract; do not add a C# parameter suffix.")]
    public static async Task<int> RunAsync(ApiOptions options, CancellationToken ct = default, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        try
        {
            var raw = ApiRequestResolver.Raw(options.Request);
            if (raw is null && options.Request.Count != 1) throw new ArgumentException("Expected an operation name or an HTTP method and path");
#pragma warning disable MA0004 // Preserve the nullable private-host lease's original context capture during disposal.
            await using var standalone = options.Standalone ? await StandaloneHostLease.StartAsync(ct, clock).ConfigureAwait(false) : null;
#pragma warning restore MA0004
            ServiceEndpoint endpoint;
            if (standalone is not null) endpoint = standalone.Endpoint;
            else if (options.Server is null) endpoint = await ServiceDaemon.EnsureAsync(ct: ct, clock: clock).ConfigureAwait(false);
            else
            {
                if (!Uri.TryCreate(options.Server, UriKind.Absolute, out var origin) || origin.Scheme is not ("http" or "https")
                    || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                    throw new ArgumentException("--server requires an HTTP(S) origin without credentials, path, query, or fragment.");
                endpoint = new(origin.GetLeftPart(UriPartial.Authority), Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVER_PASSWORD"));
                var status = await ServiceDaemon.InspectAsync(new ServiceDiscoveryOptions { Server = endpoint, Version = null, Clock = clock }, ct).ConfigureAwait(false);
                if (status?.State != ServiceState.Ready) throw new InvalidOperationException("The selected server is not ready.");
                if (status.Version != ServiceDaemon.DefaultVersion)
                    Console.Error.WriteLine($"Warning: Server at {endpoint.Url} has version {status.Version}; this client is {ServiceDaemon.DefaultVersion}. Continuing anyway.");
            }
            using var client = new ApiHttpClient(endpoint);
            var request = raw;
            if (request is null)
            {
                // User headers/data are for the final request, not schema discovery.
                using var specification = await client.SendAsync("GET", "/openapi.json", ct: ct).ConfigureAwait(false);
                if (!specification.IsSuccessStatusCode) throw new InvalidOperationException($"Failed to load OpenAPI document: HTTP {(int)specification.StatusCode}");
                var source = await specification.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var sourceLifetime = source.ConfigureAwait(true);
                using var document = await JsonDocument.ParseAsync(source, cancellationToken: ct).ConfigureAwait(false);
                request = ApiRequestResolver.Operation(document.RootElement, options.Request[0], options.Parameters);
            }
            using var response = await client.SendAsync(request.Method, request.Path, options.Data, options.Headers, ct).ConfigureAwait(false);
            await WriteResponseAsync(response, Console.Out, ct).ConfigureAwait(false);
            return 0; // Source deliberately does not reject a final HTTP error status.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Console.Error.WriteLine("API request cancelled."); return 130; }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    public static async Task WriteResponseAsync(HttpResponseMessage response, TextWriter output, CancellationToken ct = default)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var streamLifetime = stream.ConfigureAwait(true);
        // Fetch Response.text always decodes UTF-8, not the declared charset. Decode
        // incrementally so event streams are usable without inventing an SSE format.
        var wrote = false;
        var suffix = "";
        await foreach (var chunk in PipelineText.ChunksAsync(stream, new UTF8Encoding(false, false), detectBom: false,
            stripInitialBom: true, preserveSurrogates: true, cancellationToken: ct).ConfigureAwait(false))
        {
            await output.WriteAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            wrote = true;
            var tail = suffix + chunk[Math.Max(0, chunk.Length - Environment.NewLine.Length)..];
            suffix = tail.Length <= Environment.NewLine.Length ? tail : tail[^Environment.NewLine.Length..];
        }
        if (wrote && suffix != Environment.NewLine) await output.WriteAsync(Environment.NewLine.AsMemory(), ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
