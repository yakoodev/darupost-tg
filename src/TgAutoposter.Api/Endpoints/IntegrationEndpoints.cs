using TgAutoposter.Application.Abstractions;
using Microsoft.Extensions.Options;
using TgAutoposter.Infrastructure.Options;

namespace TgAutoposter.Api.Endpoints;

public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/integrations/polza/status", async (
            IAiAccountStatusClient statusClient,
            CancellationToken cancellationToken) =>
        {
            var status = await statusClient.GetStatusAsync(cancellationToken);
            return Results.Ok(status);
        }).WithTags("Integrations");

        app.MapGet("/api/integrations/worker/status", (IOptions<WorkerOptions> optionsAccessor) =>
        {
            var options = optionsAccessor.Value;
            return Results.Ok(new
            {
                options.Enabled,
                options.IntervalMinutes,
                options.MaxPostsPerRun
            });
        }).WithTags("Integrations");

        return app;
    }
}
