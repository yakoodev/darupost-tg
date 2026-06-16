namespace TgAutoposter.Domain.Common;

public enum ChannelStatus
{
    Draft = 0,
    Connected = 1,
    Disabled = 2,
    Error = 3
}

public enum ModerationMode
{
    Manual = 0,
    Automatic = 1
}

public enum ChannelRoleType
{
    Owner = 0,
    ChannelAdmin = 1,
    Moderator = 2
}

public enum SourceKind
{
    Reddit = 0,
    Web = 1,
    AiWebSearch = 2,
    Rss = 3,
    Telegram = 4
}

public enum RedditListingKind
{
    Hot = 0,
    New = 1,
    Rising = 2,
    Top = 3
}

public enum PublicationKind
{
    News = 0,
    BreakingNews = 1,
    Rumor = 2,
    Meme = 3,
    Digest = 4,
    Deal = 5,
    Trailer = 6
}

public enum FactCheckMode
{
    Soft = 0,
    Medium = 1,
    Strict = 2,
    Custom = 3
}

public enum RumorPolicy
{
    Deny = 0,
    AllowWithLabel = 1,
    WhitelistedOnly = 2,
    AlwaysManual = 3
}

public enum PostStatus
{
    CandidateFound = 0,
    WaitingFactCheck = 1,
    FactCheckFailed = 2,
    Duplicate = 3,
    GeneratingText = 4,
    GeneratingImage = 5,
    WaitingModeration = 6,
    NeedsRewrite = 7,
    Scheduled = 8,
    Published = 9,
    PublishFailed = 10,
    Rejected = 11
}

public enum DeduplicationStatus
{
    NotChecked = 0,
    Unique = 1,
    Duplicate = 2,
    Continuation = 3
}

public enum FactCheckStatus
{
    NotChecked = 0,
    Passed = 1,
    Failed = 2,
    NeedsManualReview = 3
}

public enum AiTaskType
{
    Classification = 0,
    FactCheck = 1,
    Deduplication = 2,
    PostGeneration = 3,
    Rewrite = 4,
    Translation = 5,
    Ocr = 6,
    ImageGeneration = 7,
    StructuredOutput = 8
}

public enum MediaGenerationMode
{
    None = 0,
    UseSourceImage = 1,
    GeneratePoster = 2,
    TranslateMeme = 3
}
