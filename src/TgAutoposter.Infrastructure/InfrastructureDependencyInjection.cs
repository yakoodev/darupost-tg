using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Infrastructure.Options;
using TgAutoposter.Infrastructure.Persistence;
using TgAutoposter.Infrastructure.Services;

namespace TgAutoposter.Infrastructure;

public static class InfrastructureDependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=tg_autoposter;Username=tg_autoposter;Password=tg_autoposter";

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.Configure<PolzaOptions>(configuration.GetSection("Ai:Polza"));
        services.Configure<TelegramOptions>(configuration.GetSection("Telegram"));
        services.Configure<WorkerOptions>(configuration.GetSection("Worker"));

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped<IDeduplicationService, BasicDeduplicationService>();
        services.AddScoped<IFactCheckService, BasicFactCheckService>();
        services.AddScoped<IPostTextGenerator, PostTextGenerator>();
        services.AddScoped<IModerationNotifier, TelegramModerationNotifier>();
        services.AddScoped<ITelegramPublisher, TelegramPublisher>();
        services.AddSingleton<TelegramHttpClientFactory>();
        services.AddScoped<IAutopostingPipeline, AutopostingPipeline>();
        services.AddScoped<DbSeeder>();

        services.AddHttpClient<IAiProvider, PolzaAiProvider>();
        services.AddHttpClient<IImageGenerator, PolzaImageGenerator>();
        services.AddHttpClient<IAiAccountStatusClient, PolzaAccountStatusClient>();
        services.AddHttpClient<IContentCollector, RedditCollector>();

        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
        }

        services.AddHostedService<AutopostingWorker>();
        services.AddHostedService<TelegramModerationWorker>();

        return services;
    }

    public static async Task UseInfrastructureAsync(this IHost app, CancellationToken cancellationToken = default)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InfrastructureStartup");

        for (var attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync(cancellationToken);
                await EnsureOperationalSchemaAsync(db, cancellationToken);

                var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
                await seeder.SeedAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < 30 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Database initialization failed on attempt {Attempt}. Retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private static async Task EnsureOperationalSchemaAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ModerationMessages" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NULL,
                "PostId" uuid NOT NULL,
                "ChatId" character varying(128) NOT NULL,
                "TextMessageId" integer NOT NULL,
                "ImageMessageId" integer NULL,
                "IsActive" boolean NOT NULL DEFAULT TRUE,
                "Resolution" character varying(120) NULL,
                "ResolvedAtUtc" timestamp with time zone NULL,
                CONSTRAINT "FK_ModerationMessages_Posts_PostId" FOREIGN KEY ("PostId") REFERENCES "Posts" ("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_ModerationMessages_PostId_IsActive"
            ON "ModerationMessages" ("PostId", "IsActive");

            ALTER TABLE "AiUsageRecords"
            ADD COLUMN IF NOT EXISTS "ProviderCostAmount" numeric NULL;

            ALTER TABLE "AiUsageRecords"
            ADD COLUMN IF NOT EXISTS "ProviderCostCurrency" character varying(8) NOT NULL DEFAULT 'RUB';

            ALTER TABLE "SourceCandidates"
            ADD COLUMN IF NOT EXISTS "VideoUrl" character varying(2048) NULL;

            ALTER TABLE "SourceCandidates"
            ADD COLUMN IF NOT EXISTS "MediaUrlsJson" text NULL;

            ALTER TABLE "Posts"
            ADD COLUMN IF NOT EXISTS "VideoUrl" character varying(2048) NULL;

            ALTER TABLE "Posts"
            ADD COLUMN IF NOT EXISTS "MediaUrlsJson" text NULL;
            """,
            cancellationToken);
    }
}
