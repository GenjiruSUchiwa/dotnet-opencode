namespace OpenCode.Server.Endpoints;

using OpenCode.Server.Services;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/event", async (
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
                // Send initial heartbeat / connected frame
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
                // Normal client disconnection
            }
            finally
            {
                feedService.Unsubscribe(subscriber);
            }
        });
    }
}
