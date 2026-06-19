using TgAutoposter.Domain.Common;

namespace TgAutoposter.Api.Auth;

/// <summary>
/// Endpoint filters that enforce a per-channel role. The channel id is resolved either directly from a
/// route parameter or indirectly from a post id route parameter.
/// </summary>
public static class ChannelRoleFilter
{
    public static RouteHandlerBuilder RequireChannelRole(
        this RouteHandlerBuilder builder,
        ChannelRoleType minimum,
        string channelRouteKey = "channelId")
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.RouteValues.TryGetValue(channelRouteKey, out var raw)
                || !Guid.TryParse(raw?.ToString(), out var channelId))
            {
                return Results.BadRequest(new { error = $"Не удалось определить канал из маршрута ('{channelRouteKey}')." });
            }

            var access = http.RequestServices.GetRequiredService<IChannelAccess>();
            var ok = await access.HasAtLeastAsync(http.User, channelId, minimum, http.RequestAborted);
            return ok ? await next(context) : Results.Forbid();
        });
    }

    public static RouteHandlerBuilder RequireGlobalOwner(this RouteHandlerBuilder builder)
    {
        return builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.User.IsGlobalOwner()
                ? await next(context)
                : Results.Forbid());
    }

    public static RouteHandlerBuilder RequirePostChannelRole(
        this RouteHandlerBuilder builder,
        ChannelRoleType minimum,
        string postRouteKey = "id")
    {
        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!http.Request.RouteValues.TryGetValue(postRouteKey, out var raw)
                || !Guid.TryParse(raw?.ToString(), out var postId))
            {
                return Results.BadRequest(new { error = $"Не удалось определить пост из маршрута ('{postRouteKey}')." });
            }

            var access = http.RequestServices.GetRequiredService<IChannelAccess>();
            var channelId = await access.GetChannelIdForPostAsync(postId, http.RequestAborted);
            if (channelId is null)
            {
                return Results.NotFound();
            }

            var ok = await access.HasAtLeastAsync(http.User, channelId.Value, minimum, http.RequestAborted);
            return ok ? await next(context) : Results.Forbid();
        });
    }
}
