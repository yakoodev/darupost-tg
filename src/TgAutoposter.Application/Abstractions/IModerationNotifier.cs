using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;

namespace TgAutoposter.Application.Abstractions;

public interface IModerationNotifier
{
    Task NotifyAsync(Channel channel, Post post, CancellationToken cancellationToken);
}
