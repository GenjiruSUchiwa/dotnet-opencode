namespace OpenCode.Cli.Auth;

using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;

public sealed record AuthCommandResult(int ExitCode, ImmutableArray<CredentialNotification> Notifications);
public sealed record AuthLoginOptions(string? Target = null, string? Method = null, string? ConsoleUrl = null, bool Console = false);

/// <summary>Explicit local-channel login. The shared command tree owns argument parsing.</summary>
public static class AuthCommands
{
    private sealed class SelectionCancelledException : Exception;
    public static async Task<AuthCommandResult> RunAsync(AuthLoginOptions input, CancellationToken ct = default, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        try
        {
            ct.ThrowIfCancellationRequested();
            var target = input.Console ? "opencode" : input.Target;
            var method = input.Method;
            var server = input.ConsoleUrl;
            if (input.Console)
            {
                if (server is not null && (!Uri.TryCreate(server, UriKind.Absolute, out var url)
                    || url.Scheme is not ("https" or "http") || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0))
                    throw new ArgumentException("Console URL must be HTTP(S), without credentials, query, or fragment.", nameof(input));
            }
            target ??= await ChooseAsync("Select integration", "OpenCode", "OpenAI", ct, clock).ConfigureAwait(true) == 1 ? "opencode" : "openai";
            target = target.ToLowerInvariant() switch
            {
                "opencode" => "opencode",
                "openai" => "openai",
                _ => throw new ArgumentException("Only opencode and openai OAuth login are implemented.", nameof(input))
            };
            if (target == "opencode" && method is not (null or "device"))
                throw new ArgumentException("OpenCode login supports only --method device.", nameof(input));
            if (target == "openai")
            {
                method ??= await ChooseAsync("Select login method", "ChatGPT Pro/Plus (browser)", "ChatGPT Pro/Plus (headless)", ct, clock).ConfigureAwait(true) == 1
                    ? OpenAiOAuthService.BrowserMethodId : OpenAiOAuthService.HeadlessMethodId;
                method = method.ToLowerInvariant() switch
                {
                    "chatgpt-browser" or "chatgpt pro/plus (browser)" => OpenAiOAuthService.BrowserMethodId,
                    "chatgpt-headless" or "chatgpt pro/plus (headless)" => OpenAiOAuthService.HeadlessMethodId,
                    _ => throw new ArgumentException("Choose --method chatgpt-browser or --method chatgpt-headless.", nameof(input))
                };
            }

            // The existing database owns channel path selection and schema bootstrap.
            // No caller-supplied DB path or shared-auth fallback is accepted here.
            ct.ThrowIfCancellationRequested();
            var database = new SqliteDatabase(clock: clock);
            await using var databaseLifetime = database.ConfigureAwait(true);
            // CredentialStore creates short-lived EF contexts; this remains the
            // native owner's strict bootstrap, before starting authorization.
            await using (database.CreateConnection().ConfigureAwait(true)) { }
            ct.ThrowIfCancellationRequested();
            var credentials = new CredentialStore(database);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10), clock);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            CredentialMutation mutation;
            if (target == "opencode")
            {
                using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = Timeout.InfiniteTimeSpan };
                var service = new ConsoleIntegrationService(http, credentials);
                var authorization = await service.BeginDeviceAuthorizationAsync(server, lifetime.Token).ConfigureAwait(true);
                Console.WriteLine($"Go to: {authorization.VerificationUri}");
                Console.WriteLine($"Enter code: {authorization.UserCode}");
                if (!Console.IsInputRedirected && !Console.IsOutputRedirected) OpenBrowser(authorization.VerificationUri, lifetime.Token);
                mutation = await service.CompleteDeviceAuthorizationAsync(authorization, ct: lifetime.Token).ConfigureAwait(true);
            }
            else
            {
                var service = new OpenAiOAuthService(credentials);
                if (method == OpenAiOAuthService.HeadlessMethodId)
                {
                    var authorization = await service.BeginHeadlessAuthorizationAsync(lifetime.Token).ConfigureAwait(true);
                    Console.WriteLine($"Go to: {authorization.VerificationUri}");
                    Console.WriteLine($"Enter code: {authorization.UserCode}");
                    mutation = await service.CompleteHeadlessAuthorizationAsync(authorization, ct: lifetime.Token).ConfigureAwait(true);
                }
                else mutation = await BrowserLogin.RunAsync(service, lifetime.Token, clock).ConfigureAwait(true);
            }
            Console.WriteLine("Authorization complete. Credential saved to the dotnet channel.");
            // No process-local session event is substituted for the credential bus.
            return new(0, mutation.Notifications);
        }
        catch (SelectionCancelledException)
        {
            Console.Error.WriteLine("Authorization cancelled.");
            return new(130, []);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(ct.IsCancellationRequested ? "Authorization cancelled." : "Authorization expired.");
            return new(ct.IsCancellationRequested ? 130 : 1, []);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Invalid or unsupported login arguments. Use --help for supported commands.");
            return new(2, []);
        }
        catch (Exception)
        {
            // Provider bodies, callback URLs, account labels, and exception chains
            // can contain secrets. Do not render them at the CLI boundary.
            Console.Error.WriteLine("Authorization failed. Check connectivity and callback port availability, or use headless login.");
            return new(1, []);
        }
    }

    private static async Task<int> ChooseAsync(string title, string first, string second, CancellationToken ct, TimeProvider clock)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new ArgumentException("Explicit target and method are required without an interactive terminal.", nameof(title));
        Console.WriteLine($"{title}:\n  1. {first}\n  2. {second}\nPress 1 or 2 (Esc to cancel).");
        // ReadLine on Console.In can block synchronously despite its async API.
        // Poll keys instead so cancellation never leaves a background reader alive.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable) { await Task.Delay(TimeSpan.FromMilliseconds(50), clock, ct).ConfigureAwait(true); continue; }
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.D1 or ConsoleKey.NumPad1) return 1;
            if (key is ConsoleKey.D2 or ConsoleKey.NumPad2) return 2;
            if (key == ConsoleKey.Escape) throw new SelectionCancelledException();
        }
    }

    internal static void OpenBrowser(Uri uri, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (uri.Scheme is not ("https" or "http")) throw new ArgumentException("Browser URL must be HTTP(S).", nameof(uri));
        try
        {
            // .NET 11 StartAndForget rejects UseShellExecute. URL activation may
            // reuse a browser, so retain shell launch and never bind its lifetime to ours.
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        { Console.Error.WriteLine("Could not open a browser. Open the displayed authorization URL manually."); }
    }
}
