namespace OpenCode.Core.Tools.Builtins;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Forms;
using OpenCode.Core.Permissions;
using OpenCode.Core.WebSearch;
using OpenCode.Schema;

/// <summary>Canonical permission-bearing tool leaf. Registry visibility never substitutes for execution policy.</summary>
public sealed class WebSearchTool(WebSearchRuntime websearch, IToolPermission permission, FormService? forms = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public const string Name = "websearch";
    public const string NoResults = "No search results found. Please try a different query.";
    private static readonly SemaphoreSlim ProviderSelection = new(1, 1);

    /// <summary>Use during real Location tool composition, then reload that same registration on availability/config changes.</summary>
    public async Task<ToolInfo?> CreateIfAvailableAsync(CancellationToken ct = default)
    {
        WebSearchProvider? selected;
        try { selected = await websearch.DefaultAsync(ct); }
        catch (WebSearchException error) when (error.Failure == WebSearchFailure.Disabled) { return null; }
        if (selected is not null && !await websearch.CanExecuteAsync(selected.Id, ct)) return null;
        if (selected is null && (forms is null || !websearch.CanSelect || (await websearch.AvailableProvidersAsync(ct)).Count == 0)) return null;
        return ToolInfo.FromJson(Name,
            $"Search the web using the user's selected search integration. Use this for current information beyond knowledge cutoff.\n\nThe current year is {_clock.GetLocalNow().Year}. Use this year when searching for recent information or current events.",
            JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"query":{"type":"string","description":"Websearch query"}},"required":["query"]}
                """), ExecuteAsync, JsonSerializer.Deserialize<JsonElement>("""
                {"type":"object","properties":{"provider":{"type":"string"},"results":{"type":"array","items":{"type":"object","properties":{
                    "url":{"type":"string"},"title":{"type":"string"},"content":{"type":"string"},"time":{"type":"object","properties":{"published":{"type":"number"}}}},"required":["url","time"]}}},"required":["provider","results"]}
                """), new ToolOptions(CodeMode: false));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct = default)
    {
        var query = new ToolInput(input).String("query");
        try { await permission.AssertAsync(Name, [query], ["*"], context, new Dictionary<string, object> { ["query"] = query }, ct); }
        catch (PermissionBlockedException denial) { throw new ToolExecutionException(denial.Detail, denial); }
        catch (PermissionCorrectedException correction) { throw new ToolExecutionException(correction.Feedback, correction); }
        // PermissionDeclinedException and caller interruption are deliberately not translated here.
        try
        {
            var provider = (await websearch.DefaultAsync(ct))?.Id ?? await ChooseAsync(context.SessionId, ct);
            await context.ReportProgress(new Dictionary<string, object> { ["provider"] = provider });
            var result = await websearch.QueryAsync(new(query, provider), ct);
            var output = new WebSearchToolOutput(result.ProviderId, result.Results);
            var content = result.Results.Count == 0 ? NoResults : string.Join("\n\n", result.Results.Select(item =>
            {
                var published = item.Time.Published is { } time && time != 0
                    ? "\nPublished: " + DateTimeOffset.FromUnixTimeMilliseconds((long)time).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "";
                return $"## [{item.Title ?? item.Url}]({item.Url}){published}{(string.IsNullOrEmpty(item.Content) ? "" : "\n\n" + item.Content)}";
            }));
            return new(content, JsonSerializer.SerializeToElement(output, WebSearchToolJsonContext.Default.WebSearchToolOutput),
                new Dictionary<string, object> { ["provider"] = result.ProviderId });
        }
        catch (WebSearchException error)
        {
            var message = error.Failure != WebSearchFailure.Request ? error.Message : error.StatusCode switch
            {
                429 => "Web search rate limited (HTTP 429)", 401 => "Web search authentication failed (HTTP 401)",
                { } status => $"Web search request failed (HTTP {status})", _ => $"Unable to search the web for {query}"
            };
            throw new ToolExecutionException(message, error);
        }
    }

    private async Task<string> ChooseAsync(SessionId session, CancellationToken ct)
    {
        if (forms is null || !websearch.CanSelect)
            throw new WebSearchException(WebSearchFailure.Unavailable, "Web search provider is required; provider selection forms/persistence are not composed.");
        using var lifetime = _clock.CreateLinkedCancellationTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromMinutes(1));
        try
        {
            await ProviderSelection.WaitAsync(lifetime.Token);
            try
            {
                if (await websearch.DefaultAsync(lifetime.Token) is { } selected) return selected.Id;
                var providers = await websearch.AvailableProvidersAsync(lifetime.Token);
                if (providers.Count == 0) throw new WebSearchException(WebSearchFailure.Unavailable, "No configured web search backend has a usable credential.");
                var metadata = new Dictionary<string, JsonElement> { ["kind"] = JsonSerializer.SerializeToElement("websearch.provider", OpenCodeJsonContext.Default.String) };
                var answer = await forms.AskAsync(session.Value, new("Web Search",
                    [new FormStringField { Key = "choice", Description = "Allow OpenCode to search the web for up-to-date information?", Required = true, Custom = false,
                        Options = [new("allow", $"Allow search via {string.Join(", ", providers.Select(provider => provider.Name))}"),
                            new("choose", "Choose another provider"), new("disable", "Disable web search")] }], Metadata: metadata), lifetime.Token);
                if (answer is FormCancelledState) throw new ToolExecutionException("Web search cancelled");
                if (answer is not FormAnsweredState allowed || allowed.Answer.GetValueOrDefault("choice") is not FormValue.Text choice)
                    throw new ToolContractException("Web search form returned an unsettled or invalid response.");
                if (choice.Value == "disable")
                {
                    await websearch.SelectAsync(new(Disabled: true), lifetime.Token);
                    throw new WebSearchException(WebSearchFailure.Disabled, "Web search is disabled");
                }
                if (choice.Value == "allow")
                {
                    await websearch.SelectAsync(new("random"), lifetime.Token);
                    return providers[Random.Shared.Next(providers.Count)].Id;
                }
                if (choice.Value != "choose") throw new ToolContractException("Invalid web search selection.");
                var picked = await forms.AskAsync(session.Value, new("Choose a web search provider",
                    [new FormStringField { Key = "provider", Description = "Choose a provider for web search.", Required = true, Custom = false,
                        Options = providers.Select(provider => new FormOption(provider.Id, provider.Name)).ToArray() }], Metadata: metadata), lifetime.Token);
                if (picked is FormCancelledState) throw new ToolExecutionException("Web search cancelled");
                if (picked is not FormAnsweredState value || value.Answer.GetValueOrDefault("provider") is not FormValue.Text id || !providers.Any(provider => provider.Id == id.Value))
                    throw new ToolContractException("Invalid web search provider selection.");
                await websearch.SelectAsync(new(id.Value), lifetime.Token);
                return id.Value;
            }
            finally { ProviderSelection.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && lifetime.IsCancellationRequested)
        { throw new ToolExecutionException("Web search cancelled"); }
    }
}

internal sealed record WebSearchToolOutput(string Provider, IReadOnlyList<WebSearchResult> Results);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebSearchToolOutput))]
internal partial class WebSearchToolJsonContext : JsonSerializerContext;
