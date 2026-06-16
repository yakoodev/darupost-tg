using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Posts;

public sealed class PostVersion : Entity
{
    public Guid PostId { get; set; }
    public Post? Post { get; set; }

    public int VersionNumber { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Prompt { get; set; }
    public string? Model { get; set; }
    public string Reason { get; set; } = "generation";
}
