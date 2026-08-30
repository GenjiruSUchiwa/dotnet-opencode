namespace OpenCode.Sdk;

using System.Runtime.CompilerServices;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Core.Tools;
using OpenCode.Schema;

public sealed class OpenCodeClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly IDatabase _database;
    private readonly CredentialStore _credentialStore;
    private readonly SessionStore _sessionStore;
    private readonly ProviderResolver _providerResolver;
    private readonly SessionExecutionEngine _engine;

    public SessionStore Sessions => _sessionStore;
    public CredentialStore Credentials => _credentialStore;
    public ProviderResolver Providers => _providerResolver;

    public OpenCodeClient(string? dbPath = null)
    {
        _http = new HttpClient();
        _database = new SqliteDatabase(dbPath);
        _credentialStore = new CredentialStore(_database);
        _sessionStore = new SessionStore(_database);
        _providerResolver = new ProviderResolver(_http, _credentialStore);
        var tools = new ToolRegistry(_http);
        _engine = new SessionExecutionEngine(_sessionStore, _providerResolver, tools);
    }

    public static Task<OpenCodeClient> CreateAsync(string? dbPath = null)
    {
        var client = new OpenCodeClient(dbPath);
        return Task.FromResult(client);
    }

    /// <summary>
    /// Streams a prompt to a specific session and records user and assistant messages in opencode.db.
    /// </summary>
    public IAsyncEnumerable<string> PromptAsync(
        SessionId sessionId,
        string promptText,
        string? modelId = null,
        string? variant = null,
        CancellationToken ct = default)
    {
        return _engine.PromptAsync(sessionId, promptText, modelId, variant, ct);
    }

    /// <summary>
    /// Ask a single question. Auto-creates a session in the current directory and streams the response.
    /// </summary>
    public async IAsyncEnumerable<string> AskAsync(
        string promptText,
        string? modelId = null,
        string? variant = null,
        string? directory = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var dir = directory ?? Directory.GetCurrentDirectory();
        var session = await _sessionStore.CreateSessionAsync(dir, title: promptText[..Math.Min(promptText.Length, 40)], ct: ct);

        await foreach (var chunk in _engine.PromptAsync(session.Id, promptText, modelId, variant, ct))
        {
            yield return chunk;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _database.DisposeAsync();
    }
}
