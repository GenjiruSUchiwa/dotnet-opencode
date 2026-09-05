namespace OpenCode.Server.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

internal sealed record PermissionRepliedData(
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("requestID")] PermissionId RequestId,
    [property: JsonPropertyName("reply")] PermissionReply Reply);

[JsonSerializable(typeof(PermissionRepliedData))]
internal partial class PermissionBridgeJsonContext : JsonSerializerContext;

/// <summary>
/// The Location owner runs exactly one dispatcher for its PermissionService's
/// single-reader notification channel. Dispose that service, then await this pump
/// during Location shutdown. This bridge does not create policy or own grants.
/// </summary>
public sealed class PermissionEventBridge(LocationInfo location, PermissionService permissions, IEventFeedService feed)
{
    private int _started;

    public async Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The permission event bridge already has a reader.");
        await foreach (var notification in permissions.Notifications.ReadAllAsync(ct))
        {
            // Upstream cancellation removes pending state without publishing a
            // public reply. Never invent a user rejection or permission.cancelled.
            if (notification is PermissionCancelled) continue;
            var (type, data) = notification switch
            {
                PermissionAsked asked => ("permission.asked", JsonSerializer.SerializeToElement(asked.Request,
                    OpenCodeJsonContext.Default.PermissionRequest)),
                PermissionReplied replied => ("permission.replied", JsonSerializer.SerializeToElement(
                    new PermissionRepliedData(replied.SessionId, replied.RequestId, replied.Reply),
                    PermissionBridgeJsonContext.Default.PermissionRepliedData)),
                _ => throw new NotSupportedException("Unsupported permission notification.")
            };
            feed.Publish(new OpenCodeEvent(EventId.Create(), type, feed.Clock.GetUtcNow().ToUnixTimeMilliseconds(), data,
                new LocationRef(location.Directory, location.WorkspaceId)));
        }
    }
}
