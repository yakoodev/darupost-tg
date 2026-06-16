namespace TgAutoposter.Application.Abstractions;

public interface IRealtimeNotifier
{
    Task StateChangedAsync(
        string reason,
        Guid? channelId = null,
        Guid? postId = null,
        CancellationToken cancellationToken = default);
}
