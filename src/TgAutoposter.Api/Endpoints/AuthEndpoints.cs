using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", [AllowAnonymous] async (
            LoginRequest request,
            AppDbContext db,
            TokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "Укажите email и пароль." });
            }

            var user = await db.UserAccounts
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email, cancellationToken);

            if (user is null || !user.IsEnabled || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Results.Json(new { error = "Неверный email или пароль." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var (token, expiresAt) = tokenService.Issue(user);
            var me = await BuildCurrentUserAsync(db, user.Id, cancellationToken);
            return Results.Ok(new LoginResponse(token, expiresAt, me!));
        });

        group.MapGet("/me", async (
            System.Security.Claims.ClaimsPrincipal principal,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var userId = principal.GetUserId();
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var me = await BuildCurrentUserAsync(db, userId.Value, cancellationToken);
            return me is null ? Results.Unauthorized() : Results.Ok(me);
        });

        return app;
    }

    internal static async Task<CurrentUserResponse?> BuildCurrentUserAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.UserAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        var roles = await db.ChannelRoles
            .AsNoTracking()
            .Where(role => role.UserAccountId == userId)
            .Select(role => new ChannelRoleResponse(role.ChannelId, role.Channel!.Name, role.Role))
            .ToListAsync(cancellationToken);

        return new CurrentUserResponse(user.Id, user.DisplayName, user.Email, user.IsGlobalOwner, roles);
    }
}
