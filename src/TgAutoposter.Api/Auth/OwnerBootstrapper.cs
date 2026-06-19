using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Auth;

/// <summary>
/// Ensures a global owner with login credentials exists. Idempotent — safe to run on every startup.
/// The seeder creates a passwordless owner; this upgrades it (or creates one) so the admin can log in.
/// </summary>
public static class OwnerBootstrapper
{
    public static async Task EnsureOwnerAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value;

        if (await db.UserAccounts.AnyAsync(u => u.IsGlobalOwner && u.PasswordHash != null, cancellationToken))
        {
            return;
        }

        var email = options.OwnerEmail.Trim().ToLowerInvariant();

        // Prefer upgrading whoever already holds an Owner channel-role (the seeded owner), else any user with
        // the target email, else create fresh.
        var owner = await db.UserAccounts
            .FirstOrDefaultAsync(u => u.Roles.Any(r => r.Role == ChannelRoleType.Owner), cancellationToken)
            ?? await db.UserAccounts.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (owner is null)
        {
            owner = new UserAccount { DisplayName = options.OwnerDisplayName };
            db.UserAccounts.Add(owner);
        }

        owner.Email = email;
        owner.IsGlobalOwner = true;
        owner.IsEnabled = true;
        owner.PasswordHash = PasswordHasher.Hash(options.OwnerPassword);
        if (string.IsNullOrWhiteSpace(owner.DisplayName))
        {
            owner.DisplayName = options.OwnerDisplayName;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
