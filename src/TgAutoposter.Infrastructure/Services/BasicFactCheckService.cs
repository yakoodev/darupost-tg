using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Infrastructure.Services;

public sealed class BasicFactCheckService : IFactCheckService
{
    private static readonly string[] RumorMarkers =
    [
        "rumor",
        "leak",
        "leaked",
        "reportedly",
        "insider",
        "слух",
        "утеч"
    ];

    public Task<FactCheckResult> CheckAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!publicationType.RequiresFactCheck)
        {
            return Task.FromResult(new FactCheckResult(FactCheckStatus.Passed, "Для этого типа поста фактчек отключён."));
        }

        var haystack = $"{candidate.Title}\n{candidate.Summary}\n{candidate.RawText}";
        var looksLikeRumor = RumorMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (looksLikeRumor && publicationType.RumorPolicy == RumorPolicy.Deny)
        {
            return Task.FromResult(new FactCheckResult(FactCheckStatus.Failed, "Инфоповод похож на слух, а политика типа публикации запрещает слухи."));
        }

        if (looksLikeRumor || publicationType.Kind == PublicationKind.Rumor)
        {
            return Task.FromResult(new FactCheckResult(FactCheckStatus.NeedsManualReview, "Инфоповод похож на слух или утечку. Требуется ручная проверка перед публикацией."));
        }

        var sourceText = string.IsNullOrWhiteSpace(candidate.Url) ? "без URL источника" : "есть исходный URL";
        return publicationType.FactCheckMode switch
        {
            FactCheckMode.Strict => Task.FromResult(new FactCheckResult(FactCheckStatus.NeedsManualReview, $"Строгий режим: {sourceText}, нужна ручная валидация официальности.")),
            FactCheckMode.Medium => Task.FromResult(new FactCheckResult(FactCheckStatus.NeedsManualReview, $"Средний режим: {sourceText}, нужен второй источник или ручное подтверждение.")),
            _ => Task.FromResult(new FactCheckResult(FactCheckStatus.Passed, $"Мягкий фактчек пройден: {sourceText}."))
        };
    }
}
