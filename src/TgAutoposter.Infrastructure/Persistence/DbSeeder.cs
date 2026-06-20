using Microsoft.EntityFrameworkCore;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Infrastructure.Persistence;

public sealed class DbSeeder(AppDbContext db)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Channels.AnyAsync(cancellationToken))
        {
            await EnsureDefaultPublicationTypesAsync(cancellationToken);
            await EnsureDefaultGamingFeedsAsync(cancellationToken);
            return;
        }

        var channel = new Channel
        {
            Name = "Только игры",
            TelegramUsername = "@darutests",
            Status = ChannelStatus.Connected,
            TimeZone = "Europe/Moscow",
            Language = "ru",
            Positioning = "Игровой Telegram-канал: новости, слухи, мемы, раздачи, релизы и трейлеры. Без неигровой повестки.",
            SystemPrompt = "Ты редактор Telegram-канала про игры. Пиши как человек, который быстро объясняет новость игрокам: конкретно, спокойно, без пресс-релизного тона, без ИИ-шаблонов и без неигровой повестки.",
            StyleGuide = "Русский язык, короткие абзацы, конкретика сначала. Без шаблонных секций вроде «Почему это важно», без мемных концовок, без лишнего сленга и драматизации.",
            DefaultModerationMode = ModerationMode.Manual,
            DailyPostLimit = 6,
            DailyAiBudgetLimit = 10m
        };

        channel.PublicationTypes.AddRange([
            CreatePublicationType(PublicationKind.News, "Новость", "Короткая игровая новость", 100, ModerationMode.Manual, FactCheckMode.Medium, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Rumor, "Слух / утечка", "Неофициальные игровые слухи и утечки", 80, ModerationMode.Manual, FactCheckMode.Medium, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Meme, "Мем", "Игровой мем с Reddit", 40, ModerationMode.Manual, FactCheckMode.Soft, false, MediaGenerationMode.TranslateMeme),
            CreatePublicationType(PublicationKind.Digest, "Дайджест", "Подборка нескольких инфоповодов", 60, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Deal, "Раздача / скидка", "Сильная скидка, бесплатная игра или подписочная раздача", 55, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Trailer, "Трейлер / анонс", "Трейлер, анонс, дата выхода или показ игры", 70, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster)
        ]);

        channel.Sources.AddRange([
            CreateRedditSource("r/Games", "Games", RedditListingKind.Hot, 100, 20, "News,Rumor,Digest"),
            CreateRedditSource("r/pcgaming", "pcgaming", RedditListingKind.Hot, 80, 15, "News,Digest"),
            CreateRedditSource("r/GamingLeaksAndRumours", "GamingLeaksAndRumours", RedditListingKind.Hot, 150, 25, "Rumor"),
            CreateRedditSource("r/gamememes", "gamememes", RedditListingKind.Hot, 20, 0, "Meme"),
            CreateAiWebSearchSource(),
            CreateAiTrailerSearchSource(),
            CreateRssSource("PC Gamer", "https://www.pcgamer.com/rss/", "News,Digest,Trailer"),
            CreateRssSource("Nintendo Life", "https://www.nintendolife.com/feeds/latest", "News,Digest,Trailer"),
            CreateRssSource("Rock Paper Shotgun", "https://www.rockpapershotgun.com/feed", "News,Digest"),
            CreateRssSource("The Verge Games", "https://www.theverge.com/rss/games/index.xml", "News,Digest")
        ]);

        channel.FooterLinks.AddRange([
            new FooterLink { Label = "🎮 Больше новостей", Url = "https://t.me/darutests", SortOrder = 10, IsEnabled = true },
            new FooterLink { Label = "🤖 Бот канала", Url = "https://t.me/daruautopostbot", SortOrder = 20, IsEnabled = true }
        ]);

        channel.ScheduleWindows.AddRange([
            new ScheduleWindow { StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(12, 0), MinimumIntervalMinutes = 60 },
            new ScheduleWindow { StartTime = new TimeOnly(13, 0), EndTime = new TimeOnly(15, 0), MinimumIntervalMinutes = 60 },
            new ScheduleWindow { StartTime = new TimeOnly(16, 0), EndTime = new TimeOnly(18, 0), MinimumIntervalMinutes = 60 },
            new ScheduleWindow { StartTime = new TimeOnly(19, 0), EndTime = new TimeOnly(22, 0), MinimumIntervalMinutes = 60 }
        ]);

        var owner = new UserAccount
        {
            DisplayName = "Owner",
            IsEnabled = true
        };

        channel.Roles.Add(new ChannelRole
        {
            UserAccount = owner,
            Role = ChannelRoleType.Owner
        });

        db.Channels.Add(channel);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureDefaultPublicationTypesAsync(CancellationToken cancellationToken)
    {
        var channels = await db.Channels
            .AsNoTracking()
            .Select(channel => channel.Id)
            .ToListAsync(cancellationToken);

        foreach (var channelId in channels)
        {
            var existingKinds = await db.PublicationTypes
                .AsNoTracking()
                .Where(type => type.ChannelId == channelId)
                .Select(type => type.Kind)
                .ToListAsync(cancellationToken);
            var existingSet = existingKinds.ToHashSet();

            foreach (var type in CreateDefaultPublicationTypes())
            {
                if (existingSet.Contains(type.Kind))
                {
                    continue;
                }

                type.ChannelId = channelId;
                db.PublicationTypes.Add(type);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Rumors used to ship without a poster; give existing ones a card like other types.
        await db.PublicationTypes
            .Where(type => type.Kind == PublicationKind.Rumor && type.MediaMode == MediaGenerationMode.None)
            .ExecuteUpdateAsync(updates => updates.SetProperty(type => type.MediaMode, MediaGenerationMode.GeneratePoster), cancellationToken);
    }

    private async Task EnsureDefaultGamingFeedsAsync(CancellationToken cancellationToken)
    {
        var channel = await db.Channels
            .AsNoTracking()
            .OrderBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (channel is null)
        {
            return;
        }

        var feeds = new[]
        {
            CreateRedditSource("r/Games", "Games", RedditListingKind.Hot, 100, 20, "News,Rumor,Digest"),
            CreateRedditSource("r/pcgaming", "pcgaming", RedditListingKind.Hot, 80, 15, "News,Digest,Deal"),
            CreateRedditSource("r/GamingLeaksAndRumours", "GamingLeaksAndRumours", RedditListingKind.Hot, 150, 25, "Rumor"),
            CreateRedditSource("r/gamememes", "gamememes", RedditListingKind.Hot, 20, 0, "Meme"),
            CreateRssSource("PC Gamer", "https://www.pcgamer.com/rss/", "News,Digest,Trailer"),
            CreateAiWebSearchSource(),
            CreateAiTrailerSearchSource(),
            CreateRssSource("Nintendo Life", "https://www.nintendolife.com/feeds/latest", "News,Digest,Trailer"),
            CreateRssSource("Rock Paper Shotgun", "https://www.rockpapershotgun.com/feed", "News,Digest"),
            CreateRssSource("The Verge Games", "https://www.theverge.com/rss/games/index.xml", "News,Digest")
        };

        var existingSources = await db.Sources
            .AsNoTracking()
            .Where(source => source.ChannelId == channel.Id)
            .Select(source => new { source.Url, source.Kind, source.Name, source.Subreddit })
            .ToListAsync(cancellationToken);
        var existingKeys = existingSources
            .Select(source => source.Url ?? $"{source.Kind}:{source.Subreddit ?? source.Name}")
            .Where(key => !string.IsNullOrWhiteSpace(key));
        var existingKeySet = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var feed in feeds)
        {
            var key = feed.Url ?? $"{feed.Kind}:{feed.Subreddit ?? feed.Name}";
            if (existingKeySet.Contains(key))
            {
                continue;
            }

            feed.ChannelId = channel.Id;
            db.Sources.Add(feed);
        }

        await db.SaveChangesAsync(cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.AiWebSearch && source.Name == "Polza Web Search")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.AllowedPublicationKindsCsv, "News,Digest,Deal")
                .SetProperty(source => source.Url, "fresh video game news today release patch deal giveaway gaming industry PC console Nintendo PlayStation Xbox Steam"),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.AiWebSearch && source.Name == "Polza Trailer Search")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.IsEnabled, true)
                .SetProperty(source => source.AllowedPublicationKindsCsv, "Trailer")
                .SetProperty(source => source.Url, "fresh official video game trailer today YouTube official trailer reveal gameplay showcase announcement PC PS5 Xbox Nintendo Steam"),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.Reddit && source.Subreddit == "Games")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.AllowedPublicationKindsCsv, "News,Rumor,Digest"),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.Reddit && source.Subreddit == "pcgaming")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.AllowedPublicationKindsCsv, "News,Digest,Deal"),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.Reddit && source.Subreddit == "gaming")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.IsEnabled, false)
                .SetProperty(source => source.Name, "r/gaming (legacy, too broad)")
                .SetProperty(source => source.AllowedPublicationKindsCsv, "Meme")
                .SetProperty(source => source.MinimumScore, 300)
                .SetProperty(source => source.MinimumComments, 20),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.Reddit && source.Subreddit == "gamememes")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.IsEnabled, true)
                .SetProperty(source => source.Name, "r/gamememes")
                .SetProperty(source => source.AllowedPublicationKindsCsv, "Meme")
                .SetProperty(source => source.MinimumScore, 20)
                .SetProperty(source => source.MinimumComments, 0),
                cancellationToken);

        await db.Sources
            .Where(source => source.ChannelId == channel.Id && source.Kind == SourceKind.Reddit && source.Subreddit == "gamingmemes")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(source => source.IsEnabled, false)
                .SetProperty(source => source.Name, "r/gamingmemes (404)")
                .SetProperty(source => source.AllowedPublicationKindsCsv, "Meme"),
                cancellationToken);

        await DisableDuplicateAiSourceAsync(channel.Id, "Polza Web Search", cancellationToken);
        await DisableDuplicateAiSourceAsync(channel.Id, "Polza Trailer Search", cancellationToken);
    }

    private async Task DisableDuplicateAiSourceAsync(Guid channelId, string name, CancellationToken cancellationToken)
    {
        var sourceIds = await db.Sources
            .AsNoTracking()
            .Where(source => source.ChannelId == channelId && source.Kind == SourceKind.AiWebSearch && source.Name == name)
            .OrderByDescending(source => source.IsEnabled)
            .ThenByDescending(source => source.LastCheckedAtUtc != null)
            .ThenBy(source => source.CreatedAtUtc)
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);

        if (sourceIds.Count <= 1)
        {
            return;
        }

        var keepId = sourceIds[0];
        await db.Sources
            .Where(source => source.ChannelId == channelId && source.Kind == SourceKind.AiWebSearch && source.Name == name && source.Id != keepId)
            .ExecuteUpdateAsync(updates => updates.SetProperty(source => source.IsEnabled, false), cancellationToken);
    }

    private static PublicationTypeSetting[] CreateDefaultPublicationTypes()
    {
        return
        [
            CreatePublicationType(PublicationKind.News, "Новость", "Короткая игровая новость", 100, ModerationMode.Manual, FactCheckMode.Medium, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Rumor, "Слух / утечка", "Неофициальные игровые слухи и утечки", 80, ModerationMode.Manual, FactCheckMode.Medium, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Meme, "Мем", "Игровой мем с Reddit", 40, ModerationMode.Manual, FactCheckMode.Soft, false, MediaGenerationMode.TranslateMeme),
            CreatePublicationType(PublicationKind.Digest, "Дайджест", "Подборка нескольких инфоповодов", 60, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Deal, "Раздача / скидка", "Сильная скидка, бесплатная игра или подписочная раздача", 55, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster),
            CreatePublicationType(PublicationKind.Trailer, "Трейлер / анонс", "Трейлер, анонс, дата выхода или показ игры", 70, ModerationMode.Manual, FactCheckMode.Soft, true, MediaGenerationMode.GeneratePoster)
        ];
    }

    private static PublicationTypeSetting CreatePublicationType(
        PublicationKind kind,
        string name,
        string description,
        int priority,
        ModerationMode moderationMode,
        FactCheckMode factCheckMode,
        bool requiresFactCheck = true,
        MediaGenerationMode mediaMode = MediaGenerationMode.None)
    {
        return new PublicationTypeSetting
        {
            Kind = kind,
            Name = name,
            Description = description,
            Priority = priority,
            ModerationMode = moderationMode,
            FactCheckMode = factCheckMode,
            RequiresFactCheck = requiresFactCheck,
            MediaMode = mediaMode,
            MaxTextLength = kind == PublicationKind.Meme ? 600 : 1000,
            SystemPrompt = kind switch
            {
                PublicationKind.Rumor => "Если инфоповод неофициальный, ясно обозначай это в первом абзаце.",
                PublicationKind.Meme => "Переведи или адаптируй игровой мем на русский без затягивания.",
                PublicationKind.Digest => "Собери несколько инфоповодов в короткий дайджест.",
                PublicationKind.Deal => "Пиши как полезную находку: где забрать, что дают, до какого срока, без рекламного тона.",
                PublicationKind.Trailer => "Пиши как короткий пост про трейлер или анонс: что показали, платформа/дата, почему это стоит внимания.",
                _ => "Сделай короткий Telegram-пост: факт, контекст, практический вывод. Без шаблонных подзаголовков и без ИИ-клише."
            }
        };
    }

    private static Source CreateRedditSource(
        string name,
        string subreddit,
        RedditListingKind listing,
        int minimumScore,
        int minimumComments,
        string allowedPublicationKinds)
    {
        return new Source
        {
            Name = name,
            Kind = SourceKind.Reddit,
            Subreddit = subreddit,
            RedditListing = listing,
            MinimumScore = minimumScore,
            MinimumComments = minimumComments,
            CheckEveryMinutes = 60,
            AllowedPublicationKindsCsv = allowedPublicationKinds,
            BlacklistKeywordsCsv = "weekly,daily,thread,megathread,tech support,basic questions,what are you playing,game suggestions,request thread,discussion thread,free talk",
            Language = "en",
            AllowRumors = allowedPublicationKinds.Contains("Rumor", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static Source CreateRssSource(string name, string url, string allowedPublicationKinds)
    {
        return new Source
        {
            Name = name,
            Kind = SourceKind.Rss,
            Url = url,
            CheckEveryMinutes = 30,
            MinimumScore = 0,
            MinimumComments = 0,
            AllowedPublicationKindsCsv = allowedPublicationKinds,
            BlacklistKeywordsCsv = "wordle,connections,quiz,guide,walkthrough,best settings,deal alert,hardware review,monitor,keyboard,mouse,controller,board game",
            Language = "en",
            AllowRumors = false
        };
    }

    private static Source CreateAiWebSearchSource()
    {
        return new Source
        {
            Name = "Polza Web Search",
            Kind = SourceKind.AiWebSearch,
            Url = "fresh video game news today release patch deal giveaway gaming industry PC console Nintendo PlayStation Xbox Steam",
            CheckEveryMinutes = 45,
            MinimumScore = 0,
            MinimumComments = 0,
            AllowedPublicationKindsCsv = "News,Digest,Deal",
            BlacklistKeywordsCsv = "wordle,connections,guide,walkthrough,hardware,monitor,keyboard,mouse,controller,review roundup",
            Language = "en",
            AllowRumors = false
        };
    }

    private static Source CreateAiTrailerSearchSource()
    {
        return new Source
        {
            Name = "Polza Trailer Search",
            Kind = SourceKind.AiWebSearch,
            Url = "fresh official video game trailer today YouTube official trailer reveal gameplay showcase announcement PC PS5 Xbox Nintendo Steam",
            CheckEveryMinutes = 45,
            MinimumScore = 0,
            MinimumComments = 0,
            AllowedPublicationKindsCsv = "Trailer",
            BlacklistKeywordsCsv = "review,preview,guide,walkthrough,hardware,monitor,keyboard,mouse,controller,fan trailer,concept trailer",
            Language = "en",
            AllowRumors = false
        };
    }
}
