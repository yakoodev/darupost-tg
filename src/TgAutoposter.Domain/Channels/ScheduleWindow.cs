using TgAutoposter.Domain.Common;

namespace TgAutoposter.Domain.Channels;

public sealed class ScheduleWindow : Entity
{
    public Guid ChannelId { get; set; }
    public Channel? Channel { get; set; }

    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int MinimumIntervalMinutes { get; set; } = 60;
    public bool AllowBreakingNewsBypass { get; set; }
}
