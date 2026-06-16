using Microsoft.AspNetCore.SignalR;
using TgAutoposter.Application.Abstractions;

namespace TgAutoposter.Api.Realtime;

public sealed class PostUpdatesHub : Hub
{
}

public sealed class SignalRRealtimeNotifier(IHubContext<PostUpdatesHub> hubContext) : IRealtimeNotifier
{
    public Task StateChangedAsync(
        string reason,
        Guid? channelId = null,
        Guid? postId = null,
        CancellationToken cancellationToken = default)
    {
        return hubContext.Clients.All.SendAsync(
            "stateChanged",
            new
            {
                reason,
                channelId,
                postId,
                occurredAtUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }
}
