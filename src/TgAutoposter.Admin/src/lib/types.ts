export type ModerationMode = 'Manual' | 'Automatic'
export type ChannelStatus = 'Draft' | 'Connected' | 'Disabled' | 'Error'
export type SourceKind = 'Reddit' | 'Web' | 'AiWebSearch' | 'Rss' | 'Telegram' | 'YouTube' | 'Twitter'
export type RedditListingKind = 'Hot' | 'New' | 'Rising' | 'Top'
export type FactCheckMode = 'Soft' | 'Medium' | 'Strict' | 'Custom'
export type RumorPolicy = 'Deny' | 'AllowWithLabel' | 'WhitelistedOnly' | 'AlwaysManual'
export type MediaGenerationMode = 'None' | 'UseSourceImage' | 'GeneratePoster' | 'TranslateMeme' | 'BrandCard'
export type ChannelRoleType = 'Owner' | 'ChannelAdmin' | 'Moderator'
export type ChannelMode = 'Off' | 'Moderated' | 'Auto'
export type PublicationKind = 'News' | 'BreakingNews' | 'Rumor' | 'Meme' | 'Digest' | 'Deal' | 'Trailer'
export type PostStatus =
  | 'CandidateFound' | 'WaitingFactCheck' | 'FactCheckFailed' | 'Duplicate'
  | 'GeneratingText' | 'GeneratingImage' | 'WaitingModeration' | 'NeedsRewrite'
  | 'Scheduled' | 'Published' | 'PublishFailed' | 'Rejected'

export interface ChannelRoleInfo { channelId: string; channelName: string; role: ChannelRoleType }
export interface CurrentUser {
  id: string
  displayName: string
  email?: string | null
  isGlobalOwner: boolean
  roles: ChannelRoleInfo[]
}
export interface LoginResponse { token: string; expiresAt: string; user: CurrentUser }

export interface UserListItem {
  id: string
  displayName: string
  email?: string | null
  telegramUsername?: string | null
  isEnabled: boolean
  isGlobalOwner: boolean
  hasPassword: boolean
  roles: ChannelRoleInfo[]
}

export interface Dashboard {
  channels: number
  enabledSources: number
  queueToday: number
  waitingModeration: number
  publishedToday: number
  publishedMonth: number
  rejected: number
  duplicatesFound: number
  publishErrors: number
  aiSpendToday: number
  aiSpendMonth: number
  averagePublishedPostCost: number
  providerSpendToday: number
  providerSpendMonth: number
}

export interface AiAccountStatus {
  enabled: boolean
  hasApiKey: boolean
  baseUrl: string
  defaultModel: string
  imageModel: string
  balanceRub?: number | null
  error?: string | null
}

export interface WorkerStatus { enabled: boolean; intervalMinutes: number; maxPostsPerRun: number }

export interface ChannelListItem {
  id: string
  name: string
  telegramUsername?: string | null
  status: ChannelStatus
  defaultModerationMode: ModerationMode
  dailyPostLimit: number
  isEnabled: boolean
  sourcesCount: number
  queueCount: number
}

export interface ChannelDetails {
  id: string
  name: string
  telegramUsername?: string | null
  telegramChatId?: string | null
  status: ChannelStatus
  timeZone: string
  language: string
  profileKey: string
  positioning: string
  systemPrompt: string
  styleGuide: string
  defaultModerationMode: ModerationMode
  dailyPostLimit: number
  dailyAiBudgetLimit?: number | null
  isEnabled: boolean
  digestEnabled: boolean
  digestTimeLocal: string
  digestMaxStories: number
  digestMaxDrafts: number
  lastDigestAtUtc?: string | null
}

export interface SourceItem {
  id: string
  channelId: string
  name: string
  kind: SourceKind
  url?: string | null
  isEnabled: boolean
  checkEveryMinutes: number
  subreddit?: string | null
  redditListing: RedditListingKind
  minimumScore: number
  minimumComments: number
  allowedPublicationKindsCsv?: string | null
  whitelistKeywordsCsv?: string | null
  blacklistKeywordsCsv?: string | null
  allowNsfw: boolean
  allowRumors: boolean
  requireNewsSignal: boolean
  lastCheckedAtUtc?: string | null
  lastCollectedCount: number
  lastError?: string | null
}

export interface PublicationTypeItem {
  id: string
  kind: PublicationKind
  name: string
  description: string
  isEnabled: boolean
  priority: number
  moderationMode: ModerationMode
  factCheckMode: FactCheckMode
  rumorPolicy: RumorPolicy
  maxTextLength: number
  mediaMode: MediaGenerationMode
  systemPrompt: string
  headerTemplate?: string | null
  footerTemplate?: string | null
}

export interface FooterLinkItem {
  id?: string | null
  label: string
  url: string
  sortOrder: number
  isEnabled: boolean
  publicationKindsCsv?: string | null
}

export interface ScheduleWindowItem {
  id?: string
  dayOfWeek?: number | null
  startTime: string
  endTime: string
  minimumIntervalMinutes: number
  allowBreakingNewsBypass: boolean
}

export interface PostItem {
  id: string
  channelId: string
  channelName: string
  publicationKind: string
  status: PostStatus
  sourceTitle: string
  sourceUrl?: string | null
  videoUrl?: string | null
  model?: string | null
  finalText?: string | null
  imagePath?: string | null
  mediaUrlsJson?: string | null
  factCheckStatus: string
  factCheckSummary?: string | null
  deduplicationStatus: string
  deduplicationSummary?: string | null
  scheduledForUtc?: string | null
  publishedAtUtc?: string | null
  telegramPostUrl?: string | null
  costAmount?: number | null
  costCurrency: string
  createdAtUtc: string
}

export interface PipelineRunResult {
  channelId: string
  sourcesChecked: number
  candidatesCollected: number
  postsCreated: number
  duplicatesSkipped: number
  factCheckFailed: number
  publishedThisRun: number
  publishFailed: number
  warnings: string[]
}

export interface GenerateDraftPostRequest {
  channelId: string
  publicationKind: PublicationKind
  sourceTitle: string
  sourceUrl?: string | null
  summary: string
  scheduledForUtc?: string | null
}

export interface NicheProfileSummary {
  key: string
  displayName: string
  language: string
  defaultName: string
  positioning: string
  systemPrompt: string
  styleGuide: string
  dailyPostLimit: number
  publicationTypesCount: number
  sourcesCount: number
}

export interface CandidateItem {
  id: string
  sourceId: string
  sourceName: string
  sourceKind: SourceKind
  title: string
  url?: string | null
  summary: string
  imageUrl?: string | null
  videoUrl?: string | null
  author?: string | null
  score?: number | null
  commentsCount?: number | null
  foundAtUtc: string
  createdAtUtc: string
  isConsumed: boolean
  consumedReason?: string | null
}

export interface CandidateSourceSummary {
  sourceId: string
  name: string
  kind: SourceKind
  isEnabled: boolean
  total: number
  pending: number
  lastCheckedAtUtc?: string | null
  lastCollectedCount: number
  lastError?: string | null
}

export interface CandidateList {
  hours: number
  total: number
  pending: number
  sources: CandidateSourceSummary[]
  items: CandidateItem[]
}

export interface IngestResultItem {
  sourceId: string
  sourceName: string
  collected: number
  newCandidates: number
  error?: string | null
}

export type StoryStatus = 'Open' | 'Drafted' | 'InDigest' | 'Dismissed'

export interface StoryCandidate {
  id: string
  sourceName: string
  sourceKind: SourceKind
  title: string
  url?: string | null
  score?: number | null
  foundAtUtc: string
  author?: string | null
}

export interface StoryItem {
  id: string
  title: string
  summary: string
  firstSeenAtUtc: string
  lastSeenAtUtc: string
  candidatesCount: number
  sourcesCount: number
  score: number
  isBreaking: boolean
  talentsCsv?: string | null
  kindHint?: PublicationKind | null
  status: StoryStatus
  leadCandidateId?: string | null
  postId?: string | null
  digestPostId?: string | null
  candidates: StoryCandidate[]
}

export interface DigestStatus {
  digestEnabled: boolean
  digestTimeLocal: string
  digestMaxStories: number
  digestMaxDrafts: number
  lastDigestAtUtc?: string | null
  openStories: number
}

export interface DigestRunResult {
  channelId: string
  digestPostId?: string | null
  storiesConsidered: number
  digestItems: number
  draftsCreated: number
  warnings: string[]
}

export interface TalentItem {
  id: string
  name: string
  agency?: string | null
  group?: string | null
  aliasesCsv?: string | null
  priority: number
  youTube?: string | null
  twitter?: string | null
  telegram?: string | null
  trackYouTube: boolean
  trackTwitter: boolean
  isActive: boolean
  notes?: string | null
}
