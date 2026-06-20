using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TgAutoposter.Api.Auth;
using TgAutoposter.Api.Contracts;
using TgAutoposter.Application.Abstractions;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;
using TgAutoposter.Infrastructure.Persistence;

namespace TgAutoposter.Api.Endpoints;

public static class PostEndpoints
{
    public static IEndpointRouteBuilder MapPostEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/posts").WithTags("Posts");

        group.MapGet("/", async (
            Guid? channelId,
            PostStatus? status,
            int? take,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var query = db.Posts
                .Include(post => post.Channel)
                .AsQueryable();

            if (channelId.HasValue)
            {
                query = query.Where(post => post.ChannelId == channelId);
            }

            if (status.HasValue)
            {
                query = query.Where(post => post.Status == status);
            }

            var posts = await query
                .OrderByDescending(post => post.CreatedAtUtc)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .ToListAsync(cancellationToken);

            var items = posts.Select(ToResponse).ToList();
            return Results.Ok(items);
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken cancellationToken) =>
        {
            var post = await db.Posts
                .Include(post => post.Channel)
                .Include(post => post.SourceCandidate)
                .FirstOrDefaultAsync(post => post.Id == id, cancellationToken);

            return post is null ? Results.NotFound() : Results.Ok(ToResponse(post));
        });

        group.MapPost("/generate-draft", async (
            GenerateDraftPostRequest request,
            ClaimsPrincipal principal,
            IChannelAccess access,
            AppDbContext db,
            IPostTextGenerator generator,
            IModerationNotifier moderationNotifier,
            IDateTimeProvider clock,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            if (!await access.HasAtLeastAsync(principal, request.ChannelId, ChannelRoleType.Moderator, cancellationToken))
            {
                return Results.Forbid();
            }

            var title = request.SourceTitle?.Trim() ?? string.Empty;
            var summary = request.Summary?.Trim() ?? string.Empty;
            var sourceUrl = request.SourceUrl?.Trim();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sourceTitle"] = ["Заголовок обязателен."],
                    ["summary"] = ["Описание инфоповода обязательно."]
                });
            }

            var channel = await db.Channels
                .Include(channel => channel.PublicationTypes)
                .FirstOrDefaultAsync(channel => channel.Id == request.ChannelId && channel.IsEnabled, cancellationToken);

            if (channel is null)
            {
                return Results.NotFound(new { error = "Канал не найден или выключен." });
            }

            var publicationType = channel.PublicationTypes
                .Where(type => type.IsEnabled)
                .OrderByDescending(type => type.Kind == request.PublicationKind)
                .ThenByDescending(type => type.Priority)
                .FirstOrDefault();

            if (publicationType is null)
            {
                return Results.BadRequest(new { error = "Для канала нет включённых типов публикаций." });
            }

            var candidate = new SourceCandidate
            {
                ChannelId = channel.Id,
                Title = title,
                Url = sourceUrl,
                CanonicalUrl = sourceUrl,
                Summary = summary,
                RawText = summary,
                VideoUrl = sourceUrl,
                NormalizedHash = Guid.NewGuid().ToString("N")
            };

            var generated = await generator.GenerateAsync(channel, publicationType, candidate, cancellationToken);
            var post = new Post
            {
                Channel = channel,
                ChannelId = channel.Id,
                PublicationTypeId = publicationType.Id,
                PublicationKind = publicationType.Kind,
                SourceTitle = title,
                SourceUrl = sourceUrl,
                VideoUrl = publicationType.Kind == PublicationKind.Trailer ? sourceUrl : null,
                OriginalSummary = summary,
                FactCheckStatus = FactCheckStatus.Passed,
                FactCheckSummary = "Ручной черновик из админки: фактчек пропущен.",
                DeduplicationStatus = DeduplicationStatus.Unique,
                DeduplicationSummary = "Ручной черновик из админки: дедупликация источников пропущена.",
                Prompt = generated.Prompt,
                Model = generated.Model,
                GeneratedText = generated.Text,
                FinalText = generated.Text,
                Header = generated.Header,
                Footer = generated.Footer,
                Status = PostStatus.WaitingModeration,
                ScheduledForUtc = request.ScheduledForUtc ?? clock.UtcNow.AddMinutes(5),
                CostAmount = null,
                CostCurrency = generated.CostCurrency
            };

            db.Posts.Add(post);
            db.PostVersions.Add(new PostVersion
            {
                Post = post,
                VersionNumber = 1,
                Text = generated.Text,
                Prompt = generated.Prompt,
                Model = generated.Model,
                Reason = "manual-draft"
            });

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = channel.Id,
                Post = post,
                Provider = generated.Provider,
                Model = generated.Model,
                TaskType = AiTaskType.PostGeneration,
                PromptTokens = generated.PromptTokens,
                CompletionTokens = generated.CompletionTokens,
                TotalTokens = generated.TotalTokens,
                CostAmount = null,
                CostCurrency = generated.CostCurrency,
                ProviderCostAmount = generated.CostAmount,
                ProviderCostCurrency = generated.CostCurrency,
                RequestMetadataJson = generated.UsageMetadataJson
            });

            await db.SaveChangesAsync(cancellationToken);
            await moderationNotifier.NotifyAsync(channel, post, cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-created", channel.Id, post.Id, cancellationToken);

            return Results.Created($"/api/posts/{post.Id}", ToResponse(post));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            UpdatePostRequest request,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var post = await db.Posts.FirstOrDefaultAsync(post => post.Id == id, cancellationToken);
            if (post is null)
            {
                return Results.NotFound();
            }

            var finalText = request.FinalText?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(finalText) && post.PublicationKind != PublicationKind.Meme)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["finalText"] = ["Текст поста обязателен."]
                });
            }

            post.FinalText = finalText;
            post.ScheduledForUtc = request.ScheduledForUtc;

            if (post.Status is PostStatus.NeedsRewrite or PostStatus.Rejected)
            {
                post.Status = PostStatus.WaitingModeration;
            }

            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-updated", post.ChannelId, post.Id, cancellationToken);
            return Results.NoContent();
        }).RequirePostChannelRole(ChannelRoleType.Moderator, "id");

        group.MapPost("/{id:guid}/publish", async (
            Guid id,
            ClaimsPrincipal principal,
            IAuditLogger audit,
            AppDbContext db,
            IImageGenerator imageGenerator,
            ITelegramPublisher publisher,
            IDateTimeProvider clock,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var post = await db.Posts
                .Include(post => post.Channel)
                .Include(post => post.SourceCandidate)
                .FirstOrDefaultAsync(post => post.Id == id, cancellationToken);

            if (post?.Channel is null)
            {
                return Results.NotFound();
            }

            if (ShouldGenerateImageOnPublish(post))
            {
                ImageGenerationResult image;
                try
                {
                    image = await imageGenerator.GenerateForPostAsync(post.Channel, post, cancellationToken);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { Error = $"Image generation failed: {ex.Message}" });
                }

                if (!string.IsNullOrWhiteSpace(image.ImageUrl))
                {
                    post.ImagePath = image.ImageUrl;
                }

                db.AiUsageRecords.Add(new AiUsageRecord
                {
                    ChannelId = post.ChannelId,
                    PostId = post.Id,
                    Provider = image.Provider,
                    Model = image.Model,
                    TaskType = AiTaskType.ImageGeneration,
                    CostAmount = image.CostAmount ?? AiCostDefaults.ImageGenerationRub,
                    CostCurrency = AiCostDefaults.Currency,
                    ProviderCostAmount = image.CostAmount,
                    ProviderCostCurrency = image.CostCurrency,
                    RequestMetadataJson = image.UsageMetadataJson
                });
            }

            var result = await publisher.PublishAsync(post.Channel, post, cancellationToken);
            if (!result.Success)
            {
                post.Status = PostStatus.PublishFailed;
                post.RejectionReason = result.Error;
                await db.SaveChangesAsync(cancellationToken);
                await realtimeNotifier.StateChangedAsync("post-publish-failed", post.ChannelId, post.Id, cancellationToken);
                return Results.BadRequest(new { result.Error });
            }

            post.Status = PostStatus.Published;
            post.PublishedAtUtc = clock.UtcNow;
            post.ApprovedByUserId = principal.GetUserId();
            post.TelegramMessageId = result.TelegramMessageId;
            post.TelegramPostUrl = result.PublicUrl;
            audit.Record(principal, "post.publish", nameof(Post), post.Id.ToString(), post.ChannelId);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-published", post.ChannelId, post.Id, cancellationToken);

            return Results.Ok(ToResponse(post));
        }).RequirePostChannelRole(ChannelRoleType.Moderator, "id");

        group.MapPost("/{id:guid}/rewrite", async (
            Guid id,
            AppDbContext db,
            IPostTextGenerator generator,
            IModerationNotifier moderationNotifier,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var post = await db.Posts
                .Include(post => post.Channel)
                .Include(post => post.PublicationType)
                .Include(post => post.SourceCandidate)
                .FirstOrDefaultAsync(post => post.Id == id, cancellationToken);

            if (post?.Channel is null || post.PublicationType is null)
            {
                return Results.NotFound();
            }

            var candidate = post.SourceCandidate ?? new TgAutoposter.Domain.Sources.SourceCandidate
            {
                ChannelId = post.ChannelId,
                Title = post.SourceTitle,
                Url = post.SourceUrl,
                CanonicalUrl = post.SourceUrl,
                Summary = post.OriginalSummary,
                RawText = post.OriginalSummary,
                ImageUrl = post.ImagePath,
                MediaUrlsJson = post.MediaUrlsJson,
                VideoUrl = post.VideoUrl,
                NormalizedHash = post.Id.ToString("N")
            };

            var generated = await generator.GenerateAsync(post.Channel, post.PublicationType, candidate, cancellationToken);
            post.GeneratedText = generated.Text;
            post.FinalText = generated.Text;
            post.Header = generated.Header;
            post.Footer = generated.Footer;
            post.Prompt = generated.Prompt;
            post.Model = generated.Model;
            post.CostAmount = null;
            post.CostCurrency = generated.CostCurrency;
            post.Status = PostStatus.WaitingModeration;

            var version = await db.PostVersions
                .Where(version => version.PostId == post.Id)
                .Select(version => version.VersionNumber)
                .DefaultIfEmpty()
                .MaxAsync(cancellationToken);

            db.PostVersions.Add(new PostVersion
            {
                PostId = post.Id,
                VersionNumber = version + 1,
                Text = generated.Text,
                Prompt = generated.Prompt,
                Model = generated.Model,
                Reason = "rewrite"
            });

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = post.ChannelId,
                PostId = post.Id,
                Provider = generated.Provider,
                Model = generated.Model,
                TaskType = AiTaskType.Rewrite,
                PromptTokens = generated.PromptTokens,
                CompletionTokens = generated.CompletionTokens,
                TotalTokens = generated.TotalTokens,
                CostAmount = null,
                CostCurrency = generated.CostCurrency,
                ProviderCostAmount = generated.CostAmount,
                ProviderCostCurrency = generated.CostCurrency,
                RequestMetadataJson = generated.UsageMetadataJson
            });

            await db.SaveChangesAsync(cancellationToken);
            await moderationNotifier.NotifyAsync(post.Channel, post, cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-rewritten", post.ChannelId, post.Id, cancellationToken);

            return Results.Ok(ToResponse(post));
        }).RequirePostChannelRole(ChannelRoleType.Moderator, "id");

        group.MapPost("/{id:guid}/regenerate-image", async (
            Guid id,
            AppDbContext db,
            IImageGenerator imageGenerator,
            IModerationNotifier moderationNotifier,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var post = await db.Posts
                .Include(post => post.Channel)
                .Include(post => post.SourceCandidate)
                .FirstOrDefaultAsync(post => post.Id == id, cancellationToken);

            if (post?.Channel is null)
            {
                return Results.NotFound();
            }

            if (post.Status == PostStatus.Published)
            {
                return Results.BadRequest(new { Error = "Опубликованный пост нельзя менять." });
            }

            ImageGenerationResult image;
            try
            {
                image = await imageGenerator.GenerateForPostAsync(post.Channel, post, cancellationToken);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { Error = $"Image generation failed: {ex.Message}" });
            }

            if (!string.IsNullOrWhiteSpace(image.ImageUrl))
            {
                post.ImagePath = image.ImageUrl;
            }

            post.Status = PostStatus.WaitingModeration;

            db.AiUsageRecords.Add(new AiUsageRecord
            {
                ChannelId = post.ChannelId,
                PostId = post.Id,
                Provider = image.Provider,
                Model = image.Model,
                TaskType = AiTaskType.ImageGeneration,
                CostAmount = image.CostAmount ?? AiCostDefaults.ImageGenerationRub,
                CostCurrency = AiCostDefaults.Currency,
                ProviderCostAmount = image.CostAmount,
                ProviderCostCurrency = image.CostCurrency,
                RequestMetadataJson = image.UsageMetadataJson
            });

            await db.SaveChangesAsync(cancellationToken);
            await moderationNotifier.NotifyAsync(post.Channel, post, cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-image-regenerated", post.ChannelId, post.Id, cancellationToken);

            return Results.Ok(ToResponse(post));
        }).RequirePostChannelRole(ChannelRoleType.Moderator, "id");

        group.MapPost("/{id:guid}/reject", async (
            Guid id,
            RejectPostRequest request,
            ClaimsPrincipal principal,
            IAuditLogger audit,
            AppDbContext db,
            IRealtimeNotifier realtimeNotifier,
            CancellationToken cancellationToken) =>
        {
            var post = await db.Posts.FirstOrDefaultAsync(post => post.Id == id, cancellationToken);
            if (post is null)
            {
                return Results.NotFound();
            }

            post.Status = PostStatus.Rejected;
            post.RejectionReason = request.Reason;
            post.RejectedByUserId = principal.GetUserId();
            audit.Record(principal, "post.reject", nameof(Post), post.Id.ToString(), post.ChannelId, request.Reason);
            await db.SaveChangesAsync(cancellationToken);
            await realtimeNotifier.StateChangedAsync("post-rejected", post.ChannelId, post.Id, cancellationToken);
            return Results.NoContent();
        }).RequirePostChannelRole(ChannelRoleType.Moderator, "id");

        return app;
    }

    private static PostResponse ToResponse(Post post)
    {
        return new PostResponse(
            post.Id,
            post.ChannelId,
            post.Channel?.Name ?? string.Empty,
            post.PublicationKind,
            post.Status,
            post.SourceTitle,
            post.SourceUrl,
            post.VideoUrl,
            post.Model,
            post.FinalText,
            post.ImagePath,
            post.MediaUrlsJson,
            post.FactCheckStatus,
            post.FactCheckSummary,
            post.DeduplicationStatus,
            post.DeduplicationSummary,
            post.ScheduledForUtc,
            post.PublishedAtUtc,
            post.TelegramPostUrl,
            post.CostAmount,
            post.CostCurrency,
            post.CreatedAtUtc);
    }

    private static bool ShouldGenerateImageOnPublish(Post post)
    {
        if (post.PublicationKind == PublicationKind.Trailer && !string.IsNullOrWhiteSpace(post.VideoUrl))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(post.ImagePath))
        {
            return false;
        }

        return post.PublicationKind is PublicationKind.News or PublicationKind.BreakingNews or PublicationKind.Digest or PublicationKind.Deal or PublicationKind.Trailer or PublicationKind.Meme;
    }
}
