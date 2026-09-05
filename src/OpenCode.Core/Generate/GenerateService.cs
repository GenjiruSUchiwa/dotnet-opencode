namespace OpenCode.Core.Generate;

using System.Text;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Llm;
using OpenCode.Schema;

public sealed class GenerateModelSelectionException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GenerateUnavailableException(string message, string? service = null, Exception? inner = null) : Exception(message, inner)
{
    public string? Service { get; } = service;
}

/// <summary>One stateless generation against the server's base configuration Location.
/// Owns no Session, durable events, model loop, usage ledger, or transport lifetime.</summary>
public sealed class GenerateService(ProviderResolver resolver)
{
    private readonly string _directory = Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory());

    /// <summary>Generate.text: returns assistant text exactly as collected, including an empty result.
    /// The protocol owner wraps this value in GenerateTextResponse; there is no structured-output mode.</summary>
    public async Task<string> TextAsync(string prompt, ModelRef? model = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();
        // The source handler awaits plugin readiness. Until native configuration
        // plugins can supply this catalog, reject them instead of using a partial one.
        var config = await ConfigLoader.LoadSnapshotAsync(_directory, ct).ConfigureAwait(false);
        foreach (var source in config.Sources)
        {
            if (source is ConfigSource.Document document)
                foreach (var key in new[] { "plugin", "plugins" })
                    if (document.Info[key] is { } value && value is not JsonArray { Count: 0 } && value is not JsonObject { Count: 0 })
                        throw new GenerateUnavailableException("Configured generation plugins require the native plugin runtime.", "model.catalog");
            if (source is not ConfigSource.Discovery { Entry: ConfigDirectory directory }) continue;
            foreach (var name in new[] { "plugin", "plugins" })
            {
                var path = Path.Combine(directory.Path, name);
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != FileAttributes.None && Directory.EnumerateFileSystemEntries(path).Any())
                        throw new GenerateUnavailableException("Discovered generation plugins require the native plugin runtime.", "model.catalog");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        ResolvedModel? resolved;
        try { resolved = await resolver.ResolveGenerationAsync(model, _directory, ct).ConfigureAwait(false); }
        catch (GenerationModelResolutionException error)
        {
            if (model is not null) throw new GenerateModelSelectionException(error.Message, error);
            throw new GenerateUnavailableException(error.Message, error.ProviderId, error);
        }
        catch (GenerationCredentialsException error)
        {
            throw new GenerateUnavailableException("Generation credentials are unavailable", inner: error);
        }
        catch (GenerationProviderException error)
        {
            throw new GenerateUnavailableException(error.Message, error.ProviderId, error);
        }
        if (resolved is null)
            throw new GenerateModelSelectionException(model is null ? "No model specified and no supported model is available"
                : $"Model unavailable: {model.ProviderId}/{model.Id}");

        var parts = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var order = new List<string>();
        var terminal = false;
        try
        {
            // LLM.generate folds exactly one structured stream. No Session system
            // prompt, tool registry, cache lineage, or session-affinity headers apply.
            var request = new LlmRequest(resolved.ModelId, [new LlmMessage(LlmRole.User, [new LlmContent.Text(prompt)])]);
            await foreach (var item in resolved.Client.StreamAsync(request, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                // LLMResponse.text orders fragments on first delta or authoritative
                // end, not text-start. End values may retract/replace earlier deltas.
                if (item is LlmEvent.TextDelta || item is LlmEvent.TextEnd { Text: not null })
                {
                    var id = item is LlmEvent.TextDelta delta ? delta.Id : ((LlmEvent.TextEnd)item).Id;
                    if (!parts.TryGetValue(id, out var text))
                    {
                        text = new StringBuilder();
                        parts.Add(id, text);
                        order.Add(id);
                    }
                    if (item is LlmEvent.TextDelta fragment) text.Append(fragment.Text);
                    if (item is LlmEvent.TextEnd end) { text.Clear(); text.Append(end.Text); }
                }
                // Source LLMResponse.complete accepts a finish of any reason or a
                // provider-error event. Thrown AI errors are different (mapped below).
                if (item is LlmEvent.Finish or LlmEvent.ProviderError) terminal = true;
            }
            ct.ThrowIfCancellationRequested();
            if (!terminal) throw new LlmException(new LlmFailure.InvalidProviderOutput("The provider response ended unexpectedly.", true));
            // Usage, reasoning, tool calls and provider metadata are not part of
            // Generate.text's result and must not create Session accounting/events.
            return string.Concat(order.Select(id => parts[id].ToString()));
        }
        catch (LlmException error)
        {
            throw new GenerateUnavailableException(error.Message, resolved.Selection.ProviderId, error);
        }
    }
}
