# Porting Ruleset 03: Dependency Injection and Services

This rulebook defines how to translate Effect v4 `Layer` and `Context` service graphs into idiomatic `Microsoft.Extensions.DependencyInjection` in .NET 10.

---

## 1. Context and Layer vs IServiceCollection

In TypeScript, Effect uses `Context.Tag` and `Layer` to compose explicit dependency trees.

In C#:
- Register services into `IServiceCollection`.
- Use constructor injection.
- Differentiate between **Host-wide** (Process Singletons), **Location-scoped**, and **Transient** services.

### Service Lifetime Mapping

| Effect v4 Layer Scope | .NET Lifetime | Examples in OpenCode |
| :--- | :--- | :--- |
| Global / Process-Wide Layer | `Singleton` | `IDatabaseConnection`, `IEventBus`, `ISessionExecutionCoordinator`, `IServerInfo` |
| Location Layer (`LocationServiceMap`) | `Scoped` / Factory Cache | `IFileSystem`, `IGitService`, `IToolRegistry`, `IPluginSupervisor` |
| Per-Operation / Transient | `Transient` | `SessionPromptPreparer`, `ToolInvocationContext` |

---

## 2. Host-Level Service Registration

In `OpenCode.Server` and `OpenCode.Sdk`, wire up host-level dependencies cleanly:

```csharp
namespace OpenCode.Core;

public static class CoreServiceExtensions
{
    public static IServiceCollection AddOpenCodeCore(this IServiceCollection services, Action<OpenCodeOptions>? configure = null)
    {
        var options = new OpenCodeOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // Core singletons
        services.AddSingleton<IDatabase, SqliteDatabase>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<ISessionStore, SqliteSessionStore>();
        services.AddSingleton<ISessionRunCoordinator, SessionRunCoordinator>();
        services.AddSingleton<ILocationServiceMap, LocationServiceMap>();
        services.AddSingleton<ISessionRestartService, SessionRestartService>();

        // Location-scoped factories
        services.AddSingleton<Func<LocationRef, ILocationServices>>(sp =>
        {
            var map = sp.GetRequiredService<ILocationServiceMap>();
            return location => map.GetOrCreate(location);
        });

        return services;
    }
}
```

---

## 3. Location-Scoped Services (`LocationServiceMap`)

OpenCode V2 sessions run within a specific `Location` (a local directory, workspace, or remote worktree). Services like `FileSystem`, `Git`, `ToolRegistry`, and `Instructions` are Location-scoped.

### C# Implementation Pattern
Do not rely on ambient ASP.NET HTTP request scopes for Location services, because session drains run outside HTTP request lifetimes (in background fibers/tasks).

Instead, use an explicit `LocationScope` factory:

```csharp
namespace OpenCode.Core;

public interface ILocationServices : IAsyncDisposable
{
    LocationRef Location { get; }
    IFileSystem FileSystem { get; }
    IGitService Git { get; }
    IToolRegistry Tools { get; }
    IInstructionDiscovery Instructions { get; }
}

public sealed class LocationServiceMap : ILocationServiceMap, IAsyncDisposable
{
    private readonly IServiceProvider _rootProvider;
    private readonly ConcurrentDictionary<LocationRef, Lazy<Task<ILocationServices>>> _cache = new();

    public LocationServiceMap(IServiceProvider rootProvider)
    {
        _rootProvider = rootProvider;
    }

    public async Task<ILocationServices> GetOrCreateAsync(LocationRef location)
    {
        var lazy = _cache.GetOrAdd(location, loc => new Lazy<Task<ILocationServices>>(() => CreateLocationServicesAsync(loc)));
        return await lazy.Value;
    }

    private Task<ILocationServices> CreateLocationServicesAsync(LocationRef location)
    {
        // Construct location services using DI container scope or explicit factory
        var scope = _rootProvider.CreateAsyncScope();
        var services = new LocationServices(location, scope);
        return Task.FromResult<ILocationServices>(services);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _cache.Values)
        {
            if (entry.IsValueCreated)
            {
                var services = await entry.Value;
                await services.DisposeAsync();
            }
        }
        _cache.Clear();
    }
}
```

---

## 4. Keyed Services (.NET 8+)

OpenCode supports multiple LLM providers (Anthropic, OpenAI, Gemini, Bedrock, Ollama) and workspace drivers. Use .NET Keyed Services (`[FromKeyedServices]`):

```csharp
// Registration
services.AddKeyedSingleton<ILlmProvider, AnthropicProvider>("anthropic");
services.AddKeyedSingleton<ILlmProvider, OpenAiProvider>("openai");
services.AddKeyedSingleton<ILlmProvider, OllamaProvider>("ollama");

// Resolution in Model Resolver
public sealed class ModelResolver(IServiceProvider serviceProvider)
{
    public ILlmProvider Resolve(string providerId) =>
        serviceProvider.GetKeyedService<ILlmProvider>(providerId)
        ?? throw new ProviderNotFoundException($"Unknown provider '{providerId}'");
}
```
