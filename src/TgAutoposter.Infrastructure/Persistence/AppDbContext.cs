using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TgAutoposter.Domain.Ai;
using TgAutoposter.Domain.Audit;
using TgAutoposter.Domain.Channels;
using TgAutoposter.Domain.Common;
using TgAutoposter.Domain.Posts;
using TgAutoposter.Domain.Sources;

namespace TgAutoposter.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<ChannelRole> ChannelRoles => Set<ChannelRole>();
    public DbSet<PublicationTypeSetting> PublicationTypes => Set<PublicationTypeSetting>();
    public DbSet<FooterLink> FooterLinks => Set<FooterLink>();
    public DbSet<ScheduleWindow> ScheduleWindows => Set<ScheduleWindow>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<SourceCandidate> SourceCandidates => Set<SourceCandidate>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostVersion> PostVersions => Set<PostVersion>();
    public DbSet<ModerationMessage> ModerationMessages => Set<ModerationMessage>();
    public DbSet<AiUsageRecord> AiUsageRecords => Set<AiUsageRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<ChannelStatus>().HaveConversion<string>();
        configurationBuilder.Properties<ModerationMode>().HaveConversion<string>();
        configurationBuilder.Properties<ChannelRoleType>().HaveConversion<string>();
        configurationBuilder.Properties<SourceKind>().HaveConversion<string>();
        configurationBuilder.Properties<RedditListingKind>().HaveConversion<string>();
        configurationBuilder.Properties<PublicationKind>().HaveConversion<string>();
        configurationBuilder.Properties<FactCheckMode>().HaveConversion<string>();
        configurationBuilder.Properties<RumorPolicy>().HaveConversion<string>();
        configurationBuilder.Properties<PostStatus>().HaveConversion<string>();
        configurationBuilder.Properties<DeduplicationStatus>().HaveConversion<string>();
        configurationBuilder.Properties<FactCheckStatus>().HaveConversion<string>();
        configurationBuilder.Properties<AiTaskType>().HaveConversion<string>();
        configurationBuilder.Properties<MediaGenerationMode>().HaveConversion<string>();
        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Channel>(builder =>
        {
            builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
            builder.Property(x => x.TelegramUsername).HasMaxLength(128);
            builder.Property(x => x.TelegramChatId).HasMaxLength(128);
            builder.Property(x => x.TimeZone).HasMaxLength(64);
            builder.Property(x => x.Language).HasMaxLength(16);
            builder.HasIndex(x => x.TelegramUsername);
        });

        modelBuilder.Entity<UserAccount>(builder =>
        {
            builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            builder.Property(x => x.TelegramUsername).HasMaxLength(128);
            builder.Property(x => x.Email).HasMaxLength(256);
            builder.Property(x => x.PasswordHash).HasMaxLength(512);
            builder.HasIndex(x => x.TelegramUserId).IsUnique().HasFilter("\"TelegramUserId\" IS NOT NULL");
            builder.HasIndex(x => x.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
        });

        modelBuilder.Entity<ChannelRole>(builder =>
        {
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.Roles)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(x => x.UserAccount)
                .WithMany(x => x.Roles)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(x => new { x.ChannelId, x.UserAccountId, x.Role }).IsUnique();
        });

        modelBuilder.Entity<PublicationTypeSetting>(builder =>
        {
            builder.Property(x => x.Name).HasMaxLength(120).IsRequired();
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.PublicationTypes)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.ChannelId, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<FooterLink>(builder =>
        {
            builder.Property(x => x.Label).HasMaxLength(80).IsRequired();
            builder.Property(x => x.Url).HasMaxLength(1024).IsRequired();
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.FooterLinks)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduleWindow>(builder =>
        {
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.ScheduleWindows)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Source>(builder =>
        {
            builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
            builder.Property(x => x.Url).HasMaxLength(1024);
            builder.Property(x => x.Subreddit).HasMaxLength(120);
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.Sources)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.ChannelId, x.Kind, x.Subreddit });
        });

        modelBuilder.Entity<SourceCandidate>(builder =>
        {
            builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
            builder.Property(x => x.Url).HasMaxLength(1024);
            builder.Property(x => x.CanonicalUrl).HasMaxLength(1024);
            builder.Property(x => x.VideoUrl).HasMaxLength(2048);
            builder.Property(x => x.MediaUrlsJson);
            builder.Property(x => x.NormalizedHash).HasMaxLength(128).IsRequired();
            builder.HasOne(x => x.Source)
                .WithMany()
                .HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.ChannelId, x.NormalizedHash }).IsUnique();
        });

        modelBuilder.Entity<Post>(builder =>
        {
            builder.Property(x => x.SourceTitle).HasMaxLength(512).IsRequired();
            builder.Property(x => x.SourceUrl).HasMaxLength(1024);
            builder.Property(x => x.VideoUrl).HasMaxLength(2048);
            builder.Property(x => x.MediaUrlsJson);
            builder.Property(x => x.TelegramMessageId).HasMaxLength(128);
            builder.Property(x => x.TelegramPostUrl).HasMaxLength(1024);
            builder.Property(x => x.CostCurrency).HasMaxLength(8);
            builder.HasOne(x => x.Channel)
                .WithMany(x => x.Posts)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.PublicationType)
                .WithMany()
                .HasForeignKey(x => x.PublicationTypeId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.Source)
                .WithMany()
                .HasForeignKey(x => x.SourceId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.SourceCandidate)
                .WithMany()
                .HasForeignKey(x => x.SourceCandidateId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.ApprovedByUser)
                .WithMany()
                .HasForeignKey(x => x.ApprovedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.RejectedByUser)
                .WithMany()
                .HasForeignKey(x => x.RejectedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasIndex(x => new { x.ChannelId, x.Status, x.ScheduledForUtc });
            builder.HasIndex(x => x.SourceUrl);
        });

        modelBuilder.Entity<PostVersion>(builder =>
        {
            builder.HasOne(x => x.Post)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.PostId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.PostId, x.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<ModerationMessage>(builder =>
        {
            builder.Property(x => x.ChatId).HasMaxLength(128).IsRequired();
            builder.Property(x => x.Resolution).HasMaxLength(120);
            builder.HasOne(x => x.Post)
                .WithMany()
                .HasForeignKey(x => x.PostId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasIndex(x => new { x.PostId, x.IsActive });
        });

        modelBuilder.Entity<AiUsageRecord>(builder =>
        {
            builder.Property(x => x.Provider).HasMaxLength(80).IsRequired();
            builder.Property(x => x.Model).HasMaxLength(160).IsRequired();
            builder.Property(x => x.CostCurrency).HasMaxLength(8);
            builder.Property(x => x.ProviderCostCurrency).HasMaxLength(8);
            builder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Post)
                .WithMany()
                .HasForeignKey(x => x.PostId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasIndex(x => new { x.ChannelId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<AuditLog>(builder =>
        {
            builder.Property(x => x.Action).HasMaxLength(120).IsRequired();
            builder.Property(x => x.EntityType).HasMaxLength(120).IsRequired();
            builder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.UserAccount)
                .WithMany()
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasIndex(x => new { x.ChannelId, x.CreatedAtUtc });
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        TouchEntities();
        return base.SaveChanges();
    }

    private void TouchEntities()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
            }

            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
