namespace TgAutoposter.Api.Contracts;

public sealed record ScheduleWindowResponse(
    Guid Id,
    int? DayOfWeek,
    string StartTime,
    string EndTime,
    int MinimumIntervalMinutes,
    bool AllowBreakingNewsBypass);

public sealed record ScheduleWindowRequest(
    int? DayOfWeek,
    string StartTime,
    string EndTime,
    int MinimumIntervalMinutes,
    bool AllowBreakingNewsBypass);

public sealed record UpdateScheduleRequest(IReadOnlyList<ScheduleWindowRequest> Windows);
