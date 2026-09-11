using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TgAutoposter.Application.Profiles;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Profiles;

namespace TgAutoposter.Infrastructure.Persistence;

/// <summary>
/// First-run seed: creates one channel from the profile named by <c>Seed:DefaultProfile</c> (default "gaming").
/// On later runs only fills in publication types a channel is missing according to its own profile,
/// so operators can freely delete or disable sources without them coming back.
/// </summary>
public sealed class DbSeeder(AppDbContext db, INicheProfileProvider profiles, IConfiguration configuration)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Channels.AnyAsync(cancellationToken))
        {
            await EnsurePublicationTypesAsync(cancellationToken);
            return;
        }

        var profile = profiles.Get(configuration["Seed:DefaultProfile"] ?? NicheProfileKeys.Default);
        var channel = new Channel
        {
            Status = ChannelStatus.Draft,
            DefaultModerationMode = ModerationMode.Manual
        };

        ChannelProvisioner.ProvisionNewChannel(channel, profile);

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

    private async Task EnsurePublicationTypesAsync(CancellationToken cancellationToken)
    {
        var channels = await db.Channels
            .AsNoTracking()
            .Select(channel => new { channel.Id, channel.ProfileKey })
            .ToListAsync(cancellationToken);

        foreach (var channel in channels)
        {
            var existingKinds = await db.PublicationTypes
                .AsNoTracking()
                .Where(type => type.ChannelId == channel.Id)
                .Select(type => type.Kind)
                .ToListAsync(cancellationToken);

            foreach (var type in ChannelProvisioner.MissingPublicationTypes(existingKinds, profiles.Get(channel.ProfileKey)))
            {
                type.ChannelId = channel.Id;
                db.PublicationTypes.Add(type);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
