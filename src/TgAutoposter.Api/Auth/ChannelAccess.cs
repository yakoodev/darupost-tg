using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Domain.Common;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Auth;

public interface IChannelAccess
{
    /// <summary>
    /// True when the user is a global owner or holds at least <paramref name="minimum"/> on the channel.
    /// Role privilege order (high → low): Owner, ChannelAdmin, Moderator.
    /// </summary>
    Task<bool> HasAtLeastAsync(ClaimsPrincipal user, Guid channelId, ChannelRoleType minimum, CancellationToken ct);

    Task<Guid?> GetChannelIdForPostAsync(Guid postId, CancellationToken ct);
}

public sealed class ChannelAccess(AppDbContext db) : IChannelAccess
{
    public async Task<bool> HasAtLeastAsync(ClaimsPrincipal user, Guid channelId, ChannelRoleType minimum, CancellationToken ct)
    {
        if (user.IsGlobalOwner())
        {
            return true;
        }

        var userId = user.GetUserId();
        if (userId is null)
        {
            return false;
        }

        var roles = await db.ChannelRoles
            .AsNoTracking()
            .Where(role => role.ChannelId == channelId && role.UserAccountId == userId)
            .Select(role => role.Role)
            .ToListAsync(ct);

        // Lower enum value = higher privilege (Owner=0, ChannelAdmin=1, Moderator=2).
        return roles.Any(role => role <= minimum);
    }

    public async Task<Guid?> GetChannelIdForPostAsync(Guid postId, CancellationToken ct)
    {
        var match = await db.Posts
            .AsNoTracking()
            .Where(post => post.Id == postId)
            .Select(post => new { post.ChannelId })
            .FirstOrDefaultAsync(ct);

        return match?.ChannelId;
    }
}
