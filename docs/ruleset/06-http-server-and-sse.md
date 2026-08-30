# Porting Ruleset 06: HTTP Server and Server-Sent Events (SSE)

This rulebook defines how to translate the `@opencode-ai/server` and `@opencode-ai/protocol` HTTP endpoints to ASP.NET Core Minimal APIs on Kestrel in .NET 10.

---

## 1. Minimal APIs Architecture

The server hosts the public HTTP API, the SSE event feed, and WebSocket connections for interactive PTY sessions.

### Application Setup
```csharp
namespace OpenCode.Server;

public static class ServerHost
{
    public static WebApplication CreateApp(string[] args, ServerOptions options)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        // Kestrel configuration
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port);
        });

        // Add Core dependencies
        builder.Services.AddOpenCodeCore(opt =>
        {
            opt.DatabasePath = options.DatabasePath;
        });

        builder.Services.AddSingleton<IEventFeedService, EventFeedService>();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();

        var app = builder.Build();

        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();

        // Register Route Groups
        app.MapGroup("/api/health").MapHealthRoutes();
        app.MapGroup("/api/server").MapServerRoutes();
        app.MapGroup("/api/event").MapEventRoutes();
        app.MapGroup("/api/session").MapSessionRoutes();
        app.MapGroup("/api/permission").MapPermissionRoutes();
        app.MapGroup("/api/pty").MapPtyRoutes();

        return app;
    }
}
```

---

## 2. Server-Sent Events (`/api/event`)

As specified in `specs/v2/event-stream-architecture.md`:
1. The server exposes a single `/api/event` SSE endpoint.
2. Each subscriber gets an independent dropping queue (capacity: 4,096 items).
3. If a subscriber falls behind and drops a frame, the connection is terminated with an overflow error without impacting other subscribers.

### SSE Handler Implementation
```csharp
namespace OpenCode.Server.Routes;

public static class EventRoutes
{
    public static RouteGroupBuilder MapEventRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (
            HttpContext context,
            IEventFeedService feedService,
            CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";

            var subscriber = feedService.Subscribe();

            try
            {
                // Send initial heartbeat / connection event
                await context.Response.WriteAsync("data: {\"type\":\"server.connected\"}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);

                await foreach (var frame in subscriber.ReadAllAsync(ct))
                {
                    await context.Response.WriteAsync(frame, ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal client disconnect
            }
            finally
            {
                feedService.Unsubscribe(subscriber);
            }
        });

        return group;
    }
}
```

---

## 3. Session Route Group Example

Translate the session routes from `@opencode-ai/protocol/src/groups/session.ts`:

```csharp
namespace OpenCode.Server.Routes;

public static class SessionRoutes
{
    public static RouteGroupBuilder MapSessionRoutes(this RouteGroupBuilder group)
    {
        group.MapGet("/", async (ISessionStore store, CancellationToken ct) =>
        {
            var sessions = await store.ListSessionsAsync(ct);
            return Results.Ok(sessions);
        });

        group.MapPost("/", async (
            CreateSessionRequest request,
            ISessionStore store,
            CancellationToken ct) =>
        {
            var session = await store.CreateSessionAsync(request, ct);
            return Results.Created($"/api/session/{session.Id}", session);
        });

        group.MapGet("/{id}", async (string id, ISessionStore store, CancellationToken ct) =>
        {
            var session = await store.GetSessionAsync(new SessionId(id), ct);
            return session is not null ? Results.Ok(session) : Results.NotFound();
        });

        group.MapPost("/{id}/prompt", async (
            string id,
            PromptInput input,
            ISessionCoordinator coordinator,
            CancellationToken ct) =>
        {
            var sessionId = new SessionId(id);
            var outcome = await coordinator.PromptAsync(sessionId, input, ct);
            return Results.Ok(outcome);
        });

        group.MapPost("/{id}/interrupt", async (
            string id,
            ISessionCoordinator coordinator) =>
        {
            var sessionId = new SessionId(id);
            var interrupted = await coordinator.InterruptAsync(sessionId);
            return Results.Ok(new { interrupted });
        });

        return group;
    }
}
```
