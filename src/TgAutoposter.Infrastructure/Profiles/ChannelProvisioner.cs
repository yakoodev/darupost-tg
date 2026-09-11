using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Sources;

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
            AllowRumors = template.AllowedPublicationKindsCsv?.Contains("Rumor", StringComparison.OrdinalIgnoreCase) == true,
            IsEnabled = template.IsEnabled
        };
    }

    public static string SourceKey(Source source) => source.Url ?? $"{source.Kind}:{source.Subreddit ?? source.Name}";

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
