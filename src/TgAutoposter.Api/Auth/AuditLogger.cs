using System.Security.Claims;
using TgAutoposter.Domain.Audit;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Auth;

public interface IAuditLogger
{
    /// <summary>
    /// Adds an audit entry to the change tracker. Persisted with the caller's next SaveChangesAsync,
    /// so the audited mutation and its log row commit atomically.
    /// </summary>
    void Record(ClaimsPrincipal user, string action, string entityType, string? entityId = null, Guid? channelId = null, string? detailsJson = null);
}

public sealed class AuditLogger(AppDbContext db) : IAuditLogger
{
    public void Record(ClaimsPrincipal user, string action, string entityType, string? entityId = null, Guid? channelId = null, string? detailsJson = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserAccountId = user.GetUserId(),
            ChannelId = channelId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            DetailsJson = detailsJson
        });
    }
}
