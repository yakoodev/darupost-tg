namespace TgAutoposter.Api.Contracts;

public sealed record DashboardResponse(
    int Channels,
    int EnabledSources,
    int QueueToday,
    int WaitingModeration,
    int PublishedToday,
    int PublishedMonth,
    int Rejected,
    int DuplicatesFound,
    int PublishErrors,
    decimal AiSpendToday,
    decimal AiSpendMonth,
    decimal AveragePublishedPostCost,
    decimal ProviderSpendToday,
    decimal ProviderSpendMonth);
