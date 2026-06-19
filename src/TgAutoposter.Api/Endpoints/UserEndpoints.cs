using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

/// <summary>
/// User &amp; role management. Per ТЗ §4.1 this is owner territory, so the whole group requires global owner.
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users");

        group.MapGet("/", async (AppDbContext db, CancellationToken cancellationToken) =>
        {
            var users = await db.UserAccounts
                .AsNoTracking()
                .OrderByDescending(u => u.IsGlobalOwner)
                .ThenBy(u => u.DisplayName)
                .Select(u => new
                {
                    u.Id,
                    u.DisplayName,
                    u.Email,
                    u.TelegramUsername,
                    u.IsEnabled,
                    u.IsGlobalOwner,
                    HasPassword = u.PasswordHash != null,
                    Roles = u.Roles.Select(r => new ChannelRoleResponse(r.ChannelId, r.Channel!.Name, r.Role)).ToList()
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(users.Select(u => new UserListItemResponse(
                u.Id, u.DisplayName, u.Email, u.TelegramUsername, u.IsEnabled, u.IsGlobalOwner, u.HasPassword, u.Roles)));
        }).RequireGlobalOwner();

        group.MapPost("/", async (
            CreateUserRequest request,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return Results.BadRequest(new { error = "Имя обязательно." });
            }

            var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
            if (email is not null && await db.UserAccounts.AnyAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken))
            {
                return Results.Conflict(new { error = "Пользователь с таким email уже существует." });
            }

            var user = new UserAccount
            {
                DisplayName = displayName,
                Email = email,
                TelegramUsername = string.IsNullOrWhiteSpace(request.TelegramUsername) ? null : request.TelegramUsername.Trim(),
                IsGlobalOwner = request.IsGlobalOwner,
                IsEnabled = true,
                PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? null : PasswordHasher.Hash(request.Password)
            };

            db.UserAccounts.Add(user);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/users/{user.Id}", new { user.Id });
        }).RequireGlobalOwner();

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdateUserRequest request,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? user.DisplayName : request.DisplayName.Trim();
            user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
            user.TelegramUsername = string.IsNullOrWhiteSpace(request.TelegramUsername) ? null : request.TelegramUsername.Trim();
            user.IsEnabled = request.IsEnabled;
            user.IsGlobalOwner = request.IsGlobalOwner;
            if (!string.IsNullOrWhiteSpace(request.NewPassword))
            {
                user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireGlobalOwner();

        group.MapPost("/{id:guid}/roles", async (
            Guid id,
            AssignRoleRequest request,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!await db.Channels.AnyAsync(c => c.Id == request.ChannelId, cancellationToken))
            {
                return Results.BadRequest(new { error = "Канал не найден." });
            }

            var exists = await db.ChannelRoles.AnyAsync(
                r => r.UserAccountId == id && r.ChannelId == request.ChannelId && r.Role == request.Role,
                cancellationToken);

            if (!exists)
            {
                db.ChannelRoles.Add(new ChannelRole
                {
                    UserAccountId = id,
                    ChannelId = request.ChannelId,
                    Role = request.Role
                });
                await db.SaveChangesAsync(cancellationToken);
            }

            return Results.NoContent();
        }).RequireGlobalOwner();

        group.MapDelete("/{id:guid}/roles/{roleId:guid}", async (
            Guid id,
            Guid roleId,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var role = await db.ChannelRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.UserAccountId == id, cancellationToken);
            if (role is null)
            {
                return Results.NotFound();
            }

            db.ChannelRoles.Remove(role);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireGlobalOwner();

        return app;
    }
}
