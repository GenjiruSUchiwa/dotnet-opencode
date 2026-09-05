namespace OpenCode.Cli.Commands.Run;

using System.Collections;
using System.Text;
using OpenCode.Cli.Hosting;
using OpenCode.Client;
using OpenCode.Protocol.Groups;
using OpenCode.Schema;

public static class RunCommand
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "MA0015", Justification = "Existing validation messages are emitted as CLI text/JSON errors; retain their source-compatible text.")]
    public static async Task<int> RunAsync(RunOptions options, CancellationToken ct = default, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        SessionId? sessionId = null;
        SessionHttpClient? client = null;
        StandaloneHostLease? standalone = null;
        var exitCode = 1;
        try
        {
            ServiceEndpoint endpoint;
            if (options.Standalone)
            {
                standalone = await StandaloneHostLease.StartAsync(ct, clock).ConfigureAwait(false);
                endpoint = standalone.Endpoint;
            }
            else if (options.Server is null) endpoint = await ServiceDaemon.EnsureAsync(ct: ct, clock: clock).ConfigureAwait(false);
            else
            {
                if (!Uri.TryCreate(options.Server, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new ArgumentException("--server requires an HTTP(S) origin without credentials, path, query, or fragment.");
                endpoint = new(uri.GetLeftPart(UriPartial.Authority), Environment.GetEnvironmentVariable("OPENCODE_DOTNET_SERVER_PASSWORD"));
                var status = await ServiceDaemon.InspectAsync(new ServiceDiscoveryOptions { Server = endpoint, Version = null, Clock = clock }, ct).ConfigureAwait(false);
                if (status?.State != ServiceState.Ready) throw new InvalidOperationException("The selected server is not ready.");
                if (status.Version != ServiceDaemon.DefaultVersion) Console.Error.WriteLine($"Warning: Server version {status.Version}; client version {ServiceDaemon.DefaultVersion}. Continuing anyway.");
            }
            client = new SessionHttpClient(endpoint);
            var root = Environment.GetEnvironmentVariable("PWD") ?? Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(root);
            var directory = Directory.GetCurrentDirectory();
            var message = string.Join(" ", options.Message.Select(part => part.Contains(' ') ? "\"" + part.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : part));
            if (Console.IsInputRedirected)
            {
                var piped = await Console.In.ReadToEndAsync(ct).ConfigureAwait(false);
                if (piped.Length != 0) message = message.Length == 0 ? piped : message + "\n" + piped;
            }
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("You must provide a message");
            var attachments = new List<PromptInputFileAttachment>();
            var texts = new List<string> { message };
            foreach (var name in options.Files)
            {
                var file = await RunFiles.PrepareAsync(name, directory, ct).ConfigureAwait(false);
                if (file.Text is not null) texts.Add(file.Text);
                if (file.Attachment is not null) attachments.Add(file.Attachment);
            }
            var model = string.IsNullOrEmpty(options.Model) ? null : ModelRef.Parse(options.Model);
            SessionInfo? selected = null;
            if (!string.IsNullOrEmpty(options.Session)) selected = (await client.GetAsync(SessionId.FromExisting(options.Session), ct).ConfigureAwait(false)).Data;
            if (selected is null && options.Continue)
            {
                string? cursor = null;
                do
                {
                    var page = await client.ListAsync(cursor is null
                        ? new SessionListQuery { Directory = directory, RootOnly = true, Limit = 50, Order = SessionOrder.Descending }
                        : SessionListQuery.FromCursor(cursor, 50), ct).ConfigureAwait(false);
                    selected = page.Data.FirstOrDefault(item => item.Location.Directory == directory && item.Location.WorkspaceId is null);
                    cursor = page.Data.Count == 0 ? null : page.Cursor.Next;
                } while (selected is null && cursor is not null);
            }
            if (selected is not null && options.Fork) selected = (await client.ForkAsync(selected.Id, new ForkRequestBoundaryThrough(), ct).ConfigureAwait(false)).Data;
            var resume = selected is not null;
            model ??= selected?.Model;
            var agent = options.Agent ?? selected?.Agent;
            var session = selected ?? (await client.CreateAsync(new(Agent: agent, Model: model, Location: new(directory)), ct).ConfigureAwait(false)).Data;
            sessionId = session.Id;
            if (options.Server is null && !options.Standalone && session.Location.WorkspaceId is null)
            {
                var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                    .Where(item => item.Key is string key && key is not ("OPENCODE_PASSWORD" or "OPENCODE_SERVER_PASSWORD" or "OPENCODE_DOTNET_SERVER_PASSWORD") && item.Value is string)
                    .ToDictionary(item => (string)item.Key, item => (string)item.Value!, StringComparer.Ordinal);
                await client.SetEnvironmentAsync(session.Id, environment, ct).ConfigureAwait(false);
            }
            if (!resume && options.Title is not null)
                await client.RenameAsync(session.Id, options.Title.Length != 0 ? options.Title : message[..Math.Min(50, message.Length)] + (message.Length > 50 ? "..." : ""), ct).ConfigureAwait(false);
            exitCode = await new RunConversation(client, session, options, clock).ExecuteAsync(string.Join("\n\n", texts), attachments, agent, model, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (client is not null && sessionId is { } id)
            {
                try { await client.InterruptAsync(id, ct: CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpRequestException or SessionProtocolException)
                { Console.Error.WriteLine("Unable to confirm Session interruption: " + error.Message); }
            }
            exitCode = 130;
        }
        catch (Exception error)
        {
            RunOutput.Error(options.Format, sessionId, error.Message, clock);
            exitCode = 1;
        }
        finally
        {
            try
            {
                try { client?.Dispose(); }
                finally { if (standalone is not null) await standalone.DisposeAsync().ConfigureAwait(false); }
            }
            catch (Exception error)
            {
                RunOutput.Error(options.Format, sessionId, "Private connection cleanup failed: " + error.Message, clock);
                exitCode = 1;
            }
        }
        return exitCode;
    }
}
