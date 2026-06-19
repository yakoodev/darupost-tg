using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Infrastructure.Services;

/// <summary>
/// AI-backed, mode-aware fact check (ТЗ §11). Asks the provider for a structured verdict and maps it to
/// <see cref="FactCheckStatus"/>, while hard-enforcing the rumor policy regardless of the model's answer.
/// Falls back to the heuristic <see cref="BasicFactCheckService"/> when Polza is disabled or the call fails.
/// </summary>
public sealed class AiFactCheckService(
    IAiProvider aiProvider,
    IOptions<PolzaOptions> polzaOptions,
    BasicFactCheckService fallback,
    ILogger<AiFactCheckService> logger) : IFactCheckService
{
    public async Task<FactCheckResult> CheckAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!publicationType.RequiresFactCheck)
        {
            return new FactCheckResult(FactCheckStatus.Passed, "Для этого типа поста фактчек отключён.");
        }

        if (!polzaOptions.Value.Enabled)
        {
            return await fallback.CheckAsync(channel, publicationType, candidate, cancellationToken);
        }

        try
        {
            var verdict = await RequestVerdictAsync(channel, publicationType, candidate, cancellationToken);
            return ApplyPolicy(publicationType, verdict);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AI fact check failed, falling back to heuristic for channel {ChannelId}.", channel.Id);
            return await fallback.CheckAsync(channel, publicationType, candidate, cancellationToken);
        }
    }

    private async Task<Verdict> RequestVerdictAsync(
        Channel channel,
        PublicationTypeSetting publicationType,
        SourceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var modeRule = publicationType.FactCheckMode switch
        {
            FactCheckMode.Soft => "Мягкий режим: достаточно одного вменяемого источника. Пропускай обычные игровые новости.",
            FactCheckMode.Medium => "Средний режим: нужны два независимых источника ИЛИ один официальный/крупный игровой источник. Если уверенности нет — needs_review.",
            FactCheckMode.Strict => "Строгий режим: пропускай (passed) только официально подтверждённое или из крупных СМИ. Иначе needs_review или failed.",
            _ => publicationType.SystemPrompt is { Length: > 0 } customPrompt
                ? customPrompt
                : "Пользовательский режим: оцени достоверность по здравому смыслу."
        };

        var system = new StringBuilder()
            .AppendLine("Ты фактчек-редактор игрового Telegram-канала. Оцени достоверность инфоповода перед публикацией.")
            .AppendLine(modeRule)
            .AppendLine("Учитывай, что неофициальные утечки и слухи (leak, rumor, insider, \"по слухам\") — это isRumor=true.")
            .AppendLine("Ответь СТРОГО одним JSON-объектом без markdown:")
            .AppendLine("{\"verdict\":\"passed|failed|needs_review\",\"isRumor\":true|false,\"reason\":\"кратко по-русски\"}")
            .ToString();

        var user = new StringBuilder()
            .AppendLine($"Тип публикации: {publicationType.Kind}")
            .AppendLine($"Заголовок: {candidate.Title}")
            .AppendLine($"Резюме: {candidate.Summary}")
            .AppendLine($"Источник URL: {(string.IsNullOrWhiteSpace(candidate.Url) ? "нет" : candidate.Url)}")
            .ToString();

        var response = await aiProvider.CompleteAsync(
            new AiRequest(channel.Id, AiTaskType.FactCheck, system, user, RequireJson: true),
            cancellationToken);

        return ParseVerdict(response.Text);
    }

    private static FactCheckResult ApplyPolicy(PublicationTypeSetting publicationType, Verdict verdict)
    {
        var isRumor = verdict.IsRumor || publicationType.Kind == PublicationKind.Rumor;

        // Hard rumor-policy rules take precedence over the model's verdict.
        if (isRumor)
        {
            switch (publicationType.RumorPolicy)
            {
                case RumorPolicy.Deny:
                    return new FactCheckResult(FactCheckStatus.Failed, $"Слух/утечка запрещены политикой типа. {verdict.Reason}");
                case RumorPolicy.AlwaysManual:
                case RumorPolicy.WhitelistedOnly:
                    return new FactCheckResult(FactCheckStatus.NeedsManualReview, $"Слух требует ручной проверки. {verdict.Reason}");
            }
        }

        return verdict.Status switch
        {
            "passed" when !isRumor => new FactCheckResult(FactCheckStatus.Passed, verdict.Reason),
            "passed" => new FactCheckResult(FactCheckStatus.NeedsManualReview, $"Слух допускается с пометкой, но требует подтверждения. {verdict.Reason}"),
            "failed" => new FactCheckResult(FactCheckStatus.Failed, verdict.Reason),
            _ => new FactCheckResult(FactCheckStatus.NeedsManualReview, verdict.Reason),
        };
    }

    private static Verdict ParseVerdict(string? text)
    {
        var json = ExtractJsonObject(text) ?? throw new FormatException("AI did not return a JSON object.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var verdict = root.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim().ToLowerInvariant() ?? "needs_review"
            : "needs_review";
        var isRumor = root.TryGetProperty("isRumor", out var r) &&
                      (r.ValueKind == JsonValueKind.True || (r.ValueKind == JsonValueKind.String && bool.TryParse(r.GetString(), out var b) && b));
        var reason = root.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String
            ? re.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        if (verdict is not ("passed" or "failed" or "needs_review"))
        {
            verdict = "needs_review";
        }

        return new Verdict(verdict, isRumor, string.IsNullOrWhiteSpace(reason) ? "Оценка AI-фактчека." : reason);
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private sealed record Verdict(string Status, bool IsRumor, string Reason);
}
