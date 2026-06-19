using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TgAutoposter.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TelegramUsername = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TelegramChatId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BotTokenSecretName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Positioning = table.Column<string>(type: "text", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    StyleGuide = table.Column<string>(type: "text", nullable: false),
                    DefaultModerationMode = table.Column<string>(type: "text", nullable: false),
                    DailyPostLimit = table.Column<int>(type: "integer", nullable: false),
                    DailyAiBudgetLimit = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TelegramUserId = table.Column<long>(type: "bigint", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TelegramUsername = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FooterLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PublicationKindsCsv = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FooterLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FooterLinks_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PublicationTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FrequencyPerDay = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    ModerationMode = table.Column<string>(type: "text", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: false),
                    TextTemplate = table.Column<string>(type: "text", nullable: true),
                    HeaderTemplate = table.Column<string>(type: "text", nullable: true),
                    FooterTemplate = table.Column<string>(type: "text", nullable: true),
                    RequiresPoster = table.Column<bool>(type: "boolean", nullable: false),
                    MediaMode = table.Column<string>(type: "text", nullable: false),
                    CanUseSourceImage = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresFactCheck = table.Column<bool>(type: "boolean", nullable: false),
                    FactCheckMode = table.Column<string>(type: "text", nullable: false),
                    RumorPolicy = table.Column<string>(type: "text", nullable: false),
                    MaxTextLength = table.Column<int>(type: "integer", nullable: false),
                    TimeWindowsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationTypes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PublicationTypes_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: true),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    MinimumIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    AllowBreakingNewsBypass = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduleWindows_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CheckEveryMinutes = table.Column<int>(type: "integer", nullable: false),
                    AllowedPublicationKindsCsv = table.Column<string>(type: "text", nullable: true),
                    Subreddit = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    RedditListing = table.Column<string>(type: "text", nullable: false),
                    RedditTopPeriod = table.Column<string>(type: "text", nullable: true),
                    MinimumScore = table.Column<int>(type: "integer", nullable: false),
                    MinimumComments = table.Column<int>(type: "integer", nullable: false),
                    WhitelistKeywordsCsv = table.Column<string>(type: "text", nullable: true),
                    BlacklistKeywordsCsv = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "text", nullable: false),
                    AllowNsfw = table.Column<bool>(type: "boolean", nullable: false),
                    AllowRumors = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sources_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: true),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditLogs_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AuditLogs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ChannelRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelRoles_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChannelRoles_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SourceCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CanonicalUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    RawText = table.Column<string>(type: "text", nullable: true),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    MediaUrlsJson = table.Column<string>(type: "text", nullable: true),
                    VideoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    CommentsCount = table.Column<int>(type: "integer", nullable: true),
                    FoundAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NormalizedHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    IsConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceCandidates_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SourceCandidates_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Posts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceCandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublicationKind = table.Column<string>(type: "text", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SourceTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OriginalSummary = table.Column<string>(type: "text", nullable: false),
                    RelatedSourcesJson = table.Column<string>(type: "text", nullable: true),
                    FactCheckStatus = table.Column<string>(type: "text", nullable: false),
                    FactCheckSummary = table.Column<string>(type: "text", nullable: true),
                    DeduplicationStatus = table.Column<string>(type: "text", nullable: false),
                    DeduplicationSummary = table.Column<string>(type: "text", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    GeneratedText = table.Column<string>(type: "text", nullable: true),
                    FinalText = table.Column<string>(type: "text", nullable: true),
                    Header = table.Column<string>(type: "text", nullable: true),
                    Footer = table.Column<string>(type: "text", nullable: true),
                    ImagePath = table.Column<string>(type: "text", nullable: true),
                    MediaUrlsJson = table.Column<string>(type: "text", nullable: true),
                    VideoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "text", nullable: true),
                    ScheduledForUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TelegramMessageId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TelegramPostUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CostAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    CostCurrency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Posts_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Posts_PublicationTypes_PublicationTypeId",
                        column: x => x.PublicationTypeId,
                        principalTable: "PublicationTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Posts_SourceCandidates_SourceCandidateId",
                        column: x => x.SourceCandidateId,
                        principalTable: "SourceCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Posts_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Posts_UserAccounts_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Posts_UserAccounts_RejectedByUserId",
                        column: x => x.RejectedByUserId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AiUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TaskType = table.Column<string>(type: "text", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CostAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    CostCurrency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ProviderCostAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ProviderCostCurrency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    RequestMetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiUsageRecords_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AiUsageRecords_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ModerationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TextMessageId = table.Column<int>(type: "integer", nullable: false),
                    ImageMessageId = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Resolution = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModerationMessages_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostVersions_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_ChannelId_CreatedAtUtc",
                table: "AiUsageRecords",
                columns: new[] { "ChannelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_PostId",
                table: "AiUsageRecords",
                column: "PostId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ChannelId_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "ChannelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserAccountId",
                table: "AuditLogs",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRoles_ChannelId_UserAccountId_Role",
                table: "ChannelRoles",
                columns: new[] { "ChannelId", "UserAccountId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelRoles_UserAccountId",
                table: "ChannelRoles",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_TelegramUsername",
                table: "Channels",
                column: "TelegramUsername");

            migrationBuilder.CreateIndex(
                name: "IX_FooterLinks_ChannelId",
                table: "FooterLinks",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationMessages_PostId_IsActive",
                table: "ModerationMessages",
                columns: new[] { "PostId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_ApprovedByUserId",
                table: "Posts",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_ChannelId_Status_ScheduledForUtc",
                table: "Posts",
                columns: new[] { "ChannelId", "Status", "ScheduledForUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Posts_PublicationTypeId",
                table: "Posts",
                column: "PublicationTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_RejectedByUserId",
                table: "Posts",
                column: "RejectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SourceCandidateId",
                table: "Posts",
                column: "SourceCandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SourceId",
                table: "Posts",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_SourceUrl",
                table: "Posts",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "IX_PostVersions_PostId_VersionNumber",
                table: "PostVersions",
                columns: new[] { "PostId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PublicationTypes_ChannelId_Kind",
                table: "PublicationTypes",
                columns: new[] { "ChannelId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleWindows_ChannelId",
                table: "ScheduleWindows",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceCandidates_ChannelId_NormalizedHash",
                table: "SourceCandidates",
                columns: new[] { "ChannelId", "NormalizedHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceCandidates_SourceId",
                table: "SourceCandidates",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Sources_ChannelId_Kind_Subreddit",
                table: "Sources",
                columns: new[] { "ChannelId", "Kind", "Subreddit" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_TelegramUserId",
                table: "UserAccounts",
                column: "TelegramUserId",
                unique: true,
                filter: "\"TelegramUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiUsageRecords");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "ChannelRoles");

            migrationBuilder.DropTable(
                name: "FooterLinks");

            migrationBuilder.DropTable(
                name: "ModerationMessages");

            migrationBuilder.DropTable(
                name: "PostVersions");

            migrationBuilder.DropTable(
                name: "ScheduleWindows");

            migrationBuilder.DropTable(
                name: "Posts");

            migrationBuilder.DropTable(
                name: "PublicationTypes");

            migrationBuilder.DropTable(
                name: "SourceCandidates");

            migrationBuilder.DropTable(
                name: "UserAccounts");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropTable(
                name: "Channels");
        }
    }
}
