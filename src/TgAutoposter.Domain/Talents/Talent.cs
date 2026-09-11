using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Talents;

/// <summary>
/// Registry entry for a person/persona the channel covers (VTuber talent, studio, streamer...).
/// Drives entity matching (aliases → one subject), story scoring (priority) and source auto-creation (YouTube / X).
/// </summary>
public sealed class Talent : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    /// <summary>Canonical name in original script (Gawr Gura, Kuzuha).</summary>
    public string Name { get; set; } = string.Empty;
    public string? Agency { get; set; }
    /// <summary>Generation / branch / group (hololive EN -Myth-, NIJISANJI EN Luxiem).</summary>
    public string? Group { get; set; }
    /// <summary>Comma-separated alternative spellings, nicknames, JP/RU forms.</summary>
    public string? AliasesCsv { get; set; }
    /// <summary>1 = top tier (always newsworthy), 2 = regular, 3 = niche.</summary>
    public int Priority { get; set; } = 2;
    public string? YouTube { get; set; }
    public string? Twitter { get; set; }
    public string? Telegram { get; set; }
    public bool TrackYouTube { get; set; }
    public bool TrackTwitter { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}
