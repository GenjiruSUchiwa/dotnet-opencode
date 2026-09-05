namespace OpenCode.Cli.Commands.Statistics;

using System.Globalization;
using System.Text.Json;
using OpenCode.Cli.Hosting;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public sealed record StatisticsOptions(long? Days = null, int? Year = null, bool All = false, string? Project = null,
    bool Models = false, bool Tools = false, bool Cost = false, bool Full = false, long Limit = 5,
    bool Json = false, string? Server = null, bool Standalone = false);

public static class StatisticsCommand
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "The command prints source-compatible validation errors without C# parameter suffixes.")]
    public static async Task<int> RunAsync(StatisticsOptions input, CancellationToken ct = default, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        ServiceEndpoint? endpoint = null;
        try
        {
#pragma warning disable MA0004 // Nullable private-host lease disposal preserves the existing context policy.
            await using var standalone = input.Standalone ? await StandaloneHostLease.StartAsync(ct, clock).ConfigureAwait(false) : null;
#pragma warning restore MA0004
            if (standalone is not null) endpoint = standalone.Endpoint;
            else if (input.Server is null) endpoint = await ServiceDaemon.EnsureAsync(ct: ct, clock: clock).ConfigureAwait(false);
            else
            {
                if (!Uri.TryCreate(input.Server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new ArgumentException("--server requires an HTTP(S) origin without credentials, path, query, or fragment.");
                endpoint = new(uri.GetLeftPart(UriPartial.Authority), Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVER_PASSWORD"));
                var status = await ServiceDaemon.InspectAsync(new ServiceDiscoveryOptions { Server = endpoint, Version = null, Clock = clock }, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Could not reach server at {endpoint.Url}");
                if (status.State != ServiceState.Ready) throw new InvalidOperationException("The selected server is not ready.");
                if (status.Version != ServiceDaemon.DefaultVersion)
                    Console.Error.WriteLine($"Warning: Server at {endpoint.Url} has version {status.Version}; this client is {ServiceDaemon.DefaultVersion}. Continuing anyway.");
            }
            using var client = new SessionHttpClient(endpoint);
            var zone = TimeZoneInfo.Local;
            var timezone = zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
                : throw new InvalidOperationException("The local timezone could not be resolved to an IANA identifier.");
            var range = Range(input, clock.GetUtcNow(), zone);
            ProjectId? project = input.Project is null or "." ? null : ProjectId.FromExisting(input.Project);
            if (input.Project == ".")
            {
                using var deadline = clock.CreateLinkedCancellationTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                // Existing typed project endpoint uses the same server Location resolver.
                project = (await client.CurrentProjectAsync(Directory.GetCurrentDirectory(), ct: deadline.Token).ConfigureAwait(false)).Id;
            }
            var details = input.Models || input.Tools || input.Cost || input.Full;
            var mode = input.Json || input.Tools || input.Full ? SessionStatsToolMode.Detail
                : details ? SessionStatsToolMode.None : SessionStatsToolMode.Summary;
            using var request = clock.CreateLinkedCancellationTokenSource(ct);
            request.CancelAfter(TimeSpan.FromSeconds(30));
            var stats = (await client.StatsAsync(new SessionStatsQuery(range.From, range.To, project, timezone, mode), request.Token).ConfigureAwait(false)).Data;
            // Upstream client unwraps {data}; --json prints Info, not the HTTP envelope.
            var output = input.Json
                ? JsonSerializer.Serialize(stats, new JsonSerializerOptions(OpenCodeJsonContext.Default.Options)
                    { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                : StatisticsRenderer.Render(stats, input, range.Label,
                    input.Project is null ? "all projects" : input.Project == "." ? "current project" : "selected project",
                    Console.IsOutputRedirected ? 80 : Console.WindowWidth,
                    !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null, zone);
            Console.WriteLine(output);
            return 0;
        }
        catch (SessionApiException error)
        {
            Console.Error.WriteLine(error.Payload is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : error.Message);
        }
        catch (HttpRequestException) { Console.Error.WriteLine($"Could not reach server at {endpoint?.Url ?? "the selected endpoint"}"); }
        catch (OperationCanceledException) { Console.Error.WriteLine(ct.IsCancellationRequested ? "Statistics request cancelled." : "Statistics request timed out."); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); }
        return 1;
    }

    public static (double? From, double To, string Label) Range(StatisticsOptions input, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var to = now.ToUnixTimeMilliseconds() + 1d;
        if (input.All) return (null, to, "all time");
        if (input.Days is { } days)
            return (LocalEpoch(local.Date.AddDays(-Math.Max(0, days - 1)), zone), to, days is 0 or 1 ? "today" : $"last {days} days");
        var year = input.Year ?? local.Year;
        return (LocalEpoch(new DateTime(year, 1, 1), zone), year == local.Year ? to : LocalEpoch(new DateTime(year + 1, 1, 1), zone),
            year == local.Year ? $"{year} so far" : year.ToString(CultureInfo.InvariantCulture));
    }

    private static double LocalEpoch(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // JS Date chooses the earlier instant in a fold, and advances by the gap.
        var before = local;
        while (zone.IsInvalidTime(before)) before = before.AddMinutes(-1);
        var offset = before != local ? zone.GetUtcOffset(before) : zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }
}
