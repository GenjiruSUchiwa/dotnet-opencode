namespace OpenCode.Server.Endpoints;

using System.Text.Json;
using System.Text;
using OpenCode.Schema;
using OpenCode.Server.Services;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/event", async (
            HttpContext context,
            IEventFeedService feedService,
            IHostApplicationLifetime lifetime,
            CancellationToken ct) =>
        {
            // Idle SSE reads must end during host drain, before the feed service is disposed.
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            var cancellationToken = shutdown.Token;
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            using var writes = new SemaphoreSlim(1, 1);

            var subscriber = feedService.Subscribe();

            try
            {
                // The connection-local Protocol frame has no created or durable envelope.
                var connected = new ServerConnectedFrame(EventId.Create());
                var json = JsonSerializer.Serialize(connected, OpenCodeJsonContext.Default.ServerConnectedFrame);
                await WriteAsync($"data: {json}\n\n");
                var live = LiveAsync();
                var heartbeat = HeartbeatAsync();
                try { await Task.WhenAny(live, heartbeat); }
                finally
                {
                    await shutdown.CancelAsync();
                    await Task.WhenAll(live, heartbeat);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client disconnection or application shutdown.
            }
            finally
            {
                feedService.Unsubscribe(subscriber);
            }

            async Task WriteAsync(string frame)
            {
                await writes.WaitAsync(cancellationToken);
                try
                {
                    if (!context.Response.HasStarted) await context.Response.StartAsync(cancellationToken);
                    var writer = context.Response.BodyWriter;
                    var memory = writer.GetMemory(Encoding.UTF8.GetByteCount(frame));
                    writer.Advance(Encoding.UTF8.GetBytes(frame, memory.Span));
                    var flushed = await writer.FlushAsync(cancellationToken);
                    if (flushed.IsCanceled) throw new OperationCanceledException(cancellationToken);
                }
                finally { writes.Release(); }
            }

            async Task LiveAsync()
            {
                await foreach (var frame in subscriber.ReadAllAsync(cancellationToken)) await WriteAsync(frame);
            }

            async Task HeartbeatAsync()
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), feedService.Clock);
                while (await timer.WaitForNextTickAsync(cancellationToken)) await WriteAsync(": heartbeat\n\n");
            }
        });
    }
}
