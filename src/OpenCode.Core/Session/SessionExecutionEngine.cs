namespace OpenCode.Core.Session;

using System.Runtime.CompilerServices;
using System.Text;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Schema;

public sealed class SessionExecutionEngine
{
    private readonly SessionStore _sessionStore;
    private readonly ProviderResolver _providerResolver;

    public SessionExecutionEngine(SessionStore sessionStore, ProviderResolver providerResolver)
    {
        _sessionStore = sessionStore;
        _providerResolver = providerResolver;
    }

    public async IAsyncEnumerable<string> PromptAsync(
        SessionId sessionId,
        string promptText,
        string? modelId = null,
        string? variant = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1. Record User Message in Database
        var userMsgId = MessageId.Create();
        var userMsg = new UserPromptMessage
        {
            Id = userMsgId,
            CreatedAt = DateTimeOffset.UtcNow,
            Text = promptText
        };
        await _sessionStore.AddMessageAsync(sessionId, userMsg, ct);

        // 2. Resolve LLM Provider
        var resolved = await _providerResolver.ResolveAsync(modelId, variant, ct);

        // 3. Prepare History and Stream Response
        var messages = new List<LlmChatMessage>
        {
            new("system", "You are an AI engineering agent inside opencode-dotnet."),
            new("user", promptText)
        };

        var assistantText = new StringBuilder();
        await foreach (var chunk in resolved.Client.StreamChatAsync(messages, resolved.ModelId, resolved.GenerationConfig, ct))
        {
            assistantText.Append(chunk);
            yield return chunk;
        }

        // 4. Record Assistant Message in Database
        var assistantMsgId = MessageId.Create();
        var assistantMsg = new AssistantMessage
        {
            Id = assistantMsgId,
            CreatedAt = DateTimeOffset.UtcNow,
            Text = assistantText.ToString(),
            ModelId = resolved.ModelId,
            ProviderId = resolved.Client.ProviderId
        };
        await _sessionStore.AddMessageAsync(sessionId, assistantMsg, ct);
    }
}
