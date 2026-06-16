using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Posts;

namespace TgAutoposter.Application.Abstractions;

public interface ITelegramPublisher
{
    Task<PublishResult> PublishAsync(Channel channel, Post post, CancellationToken cancellationToken);
}

public sealed record PublishResult(bool Success, string? TelegramMessageId, string? PublicUrl, string? Error);
