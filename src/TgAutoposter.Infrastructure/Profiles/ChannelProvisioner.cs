using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Domain.Talents;

namespace TgAutoposter.Infrastructure.Profiles;

/// <summary>Materialises a niche profile's defaults (prompts, publication types, sources, footer, schedule) onto a channel.</summary>
public static class ChannelProvisioner
{
    /// <summary>Fills empty channel text fields from the profile. Never overwrites non-empty values.</summary>
    public static void ApplyChannelDefaults(Channel channel, NicheProfile profile)
    {
        channel.ProfileKey = profile.Key;
        if (string.IsNullOrWhiteSpace(channel.Name))
        {
            channel.Name = profile.Channel.Name;
        }

        if (string.IsNullOrWhiteSpace(channel.TimeZone))
        {
            channel.TimeZone = profile.Channel.TimeZone;
        }

        if (string.IsNullOrWhiteSpace(channel.Language))
        {
            channel.Language = profile.Language;
        }

        if (string.IsNullOrWhiteSpace(channel.Positioning))
        {
            channel.Positioning = profile.Channel.Positioning;
        }

        if (string.IsNullOrWhiteSpace(channel.SystemPrompt))
        {
            channel.SystemPrompt = profile.Channel.SystemPrompt;
        }

        if (string.IsNullOrWhiteSpace(channel.StyleGuide))
        {
            channel.StyleGuide = profile.Channel.StyleGuide;
        }

        if (channel.DailyPostLimit <= 0)
        {
            channel.DailyPostLimit = profile.Channel.DailyPostLimit;
        }

        channel.DailyAiBudgetLimit ??= profile.Channel.DailyAiBudgetLimit;
    }

    /// <summary>Adds publication types from the profile for kinds the channel does not have yet.</summary>
    public static IReadOnlyList<PublicationTypeSetting> MissingPublicationTypes(IEnumerable<PublicationKind> existingKinds, NicheProfile profile)
    {
        var existing = existingKinds.ToHashSet();
        return profile.PublicationTypes
            .Where(template => !existing.Contains(template.Kind))
            .Select(CreatePublicationType)
            .ToList();
    }

    public static PublicationTypeSetting CreatePublicationType(NichePublicationType template)
    {
        return new PublicationTypeSetting
        {
            Kind = template.Kind,
            Name = template.Name,
            Description = template.Description,
            Priority = template.Priority,
            ModerationMode = template.ModerationMode,
            FactCheckMode = template.FactCheckMode,
            RumorPolicy = template.RumorPolicy,
            RequiresFactCheck = template.RequiresFactCheck,
            MediaMode = template.MediaMode,
            MaxTextLength = template.MaxTextLength,
            SystemPrompt = template.SystemPrompt,
            IsEnabled = template.IsEnabled
        };
    }

    public static Source CreateSource(NicheSource template)
    {
        return new Source
        {
            Name = template.Name,
            Kind = template.Kind,
            Url = template.Url,
            Subreddit = template.Subreddit,
            RedditListing = template.RedditListing,
            MinimumScore = template.MinimumScore,
            MinimumComments = template.MinimumComments,
            CheckEveryMinutes = template.CheckEveryMinutes,
            AllowedPublicationKindsCsv = template.AllowedPublicationKindsCsv,
            WhitelistKeywordsCsv = template.WhitelistKeywordsCsv,
            BlacklistKeywordsCsv = template.BlacklistKeywordsCsv,
            Language = template.Language,
            AllowNsfw = template.AllowNsfw,
            RequireNewsSignal = template.RequireNewsSignal ?? template.Kind is not (SourceKind.Telegram or SourceKind.YouTube),
            AllowRumors = template.AllowedPublicationKindsCsv?.Contains("Rumor", StringComparison.OrdinalIgnoreCase) == true,
            IsEnabled = template.IsEnabled
        };
    }

    public static string SourceKey(Source source) => source.Url ?? $"{source.Kind}:{source.Subreddit ?? source.Name}";

    public sealed record SyncResult(
        int SourcesAdded,
        int TalentsAdded,
        int TypesAdded,
        IReadOnlyList<Source> NewSources,
        IReadOnlyList<Talent> NewTalents,
        IReadOnlyList<PublicationTypeSetting> NewTypes);

    /// <summary>
    /// Brings an existing channel up to date with its profile: adds sources / talents / publication types
    /// that the channel does not have yet. Never touches or re-enables what the operator already changed.
    /// </summary>
    public static SyncResult SyncFromProfile(Channel channel, NicheProfile profile)
    {
        var types = MissingPublicationTypes(channel.PublicationTypes.Select(type => type.Kind), profile);
        foreach (var type in types)
        {
            type.ChannelId = channel.Id;
        }

        var existingKeys = channel.Sources.Select(SourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSources = new List<Source>();
        foreach (var template in profile.Sources)
        {
            var source = CreateSource(template);
            if (existingKeys.Add(SourceKey(source)))
            {
                source.ChannelId = channel.Id;
                newSources.Add(source);
            }
        }

        var existingNames = channel.Talents.Select(talent => talent.Name.ToLowerInvariant()).ToHashSet();
        var newTalents = new List<Talent>();
        foreach (var template in profile.Talents.Where(template => !string.IsNullOrWhiteSpace(template.Name)))
        {
            if (!existingNames.Add(template.Name.Trim().ToLowerInvariant()))
            {
                continue;
            }

            newTalents.Add(new Talent
            {
                ChannelId = channel.Id,
                Name = template.Name.Trim(),
                Agency = template.Agency,
                Group = template.Group,
                AliasesCsv = template.Aliases,
                Priority = Math.Clamp(template.Priority, 1, 3),
                YouTube = template.YouTube,
                Twitter = template.Twitter,
                Telegram = template.Telegram,
                IsActive = true
            });
        }

        return new SyncResult(newSources.Count, newTalents.Count, types.Count, newSources, newTalents, types);
    }

    /// <summary>Populates a brand-new channel with everything the profile ships with.</summary>
    public static void ProvisionNewChannel(Channel channel, NicheProfile profile)
    {
        ApplyChannelDefaults(channel, profile);

        channel.PublicationTypes.AddRange(MissingPublicationTypes(channel.PublicationTypes.Select(type => type.Kind), profile));

        var existingKeys = channel.Sources.Select(SourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var template in profile.Sources)
        {
            var source = CreateSource(template);
            if (existingKeys.Add(SourceKey(source)))
            {
                channel.Sources.Add(source);
            }
        }

        if (channel.FooterLinks.Count == 0)
        {
            channel.FooterLinks.AddRange(profile.FooterLinks.Select(link => new FooterLink
            {
                Label = link.Label,
                Url = link.Url,
                SortOrder = link.SortOrder,
                IsEnabled = true
            }));
        }

        channel.Talents.AddRange(profile.Talents
            .Where(template => !string.IsNullOrWhiteSpace(template.Name))
            .Select(template => new Talent
            {
                Name = template.Name.Trim(),
                Agency = template.Agency,
                Group = template.Group,
                AliasesCsv = template.Aliases,
                Priority = Math.Clamp(template.Priority, 1, 3),
                YouTube = template.YouTube,
                Twitter = template.Twitter,
                Telegram = template.Telegram,
                IsActive = true
            }));

        if (channel.ScheduleWindows.Count == 0)
        {
            foreach (var window in profile.ScheduleWindows)
            {
                if (TimeOnly.TryParse(window.Start, out var start) && TimeOnly.TryParse(window.End, out var end))
                {
                    channel.ScheduleWindows.Add(new ScheduleWindow
                    {
                        StartTime = start,
                        EndTime = end,
                        MinimumIntervalMinutes = window.MinimumIntervalMinutes
                    });
                }
            }
        }
    }
}
