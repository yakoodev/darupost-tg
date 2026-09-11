using TgAutoposter.Application.Profiles;

namespace TgAutoposter.Api.Endpoints;

public static class ProfileEndpoints
{
    public sealed record NicheProfileResponse(
        string Key,
        string DisplayName,
        string Language,
        string DefaultName,
        string Positioning,
        string SystemPrompt,
        string StyleGuide,
        int DailyPostLimit,
        int PublicationTypesCount,
        int SourcesCount);

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles").WithTags("Profiles").RequireAuthorization();

        group.MapGet("/", (INicheProfileProvider profiles) =>
        {
            var items = profiles.All()
                .Select(profile => new NicheProfileResponse(
                    profile.Key,
                    profile.DisplayName,
                    profile.Language,
                    profile.Channel.Name,
                    profile.Channel.Positioning,
                    profile.Channel.SystemPrompt,
                    profile.Channel.StyleGuide,
                    profile.Channel.DailyPostLimit,
                    profile.PublicationTypes.Count,
                    profile.Sources.Count))
                .ToList();

            return Results.Ok(items);
        });

        return app;
    }
}
