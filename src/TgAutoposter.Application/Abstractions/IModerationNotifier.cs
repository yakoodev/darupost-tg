using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;

namespace TgAutoposter.Application.Abstractions;

public interface IModerationNotifier
{
    Task NotifyAsync(Channel channel, Post post, CancellationToken cancellationToken);

    /// <summary>Closes the post's active moderation messages: removes buttons, deletes the image preview, shows <paramref name="text"/>.</summary>
    Task ResolveAsync(Guid postId, string text, CancellationToken cancellationToken);
}
