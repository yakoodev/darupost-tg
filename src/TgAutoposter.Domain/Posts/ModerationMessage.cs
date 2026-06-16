namespace TgAutoposter.Domain.Posts;

public sealed class ModerationMessage : Common.Entity
{
    public Guid PostId { get; set; }
    public Post? Post { get; set; }

    public string ChatId { get; set; } = string.Empty;
    public int TextMessageId { get; set; }
    public int? ImageMessageId { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Resolution { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
