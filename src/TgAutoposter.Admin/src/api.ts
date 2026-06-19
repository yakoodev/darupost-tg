export const API_BASE = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'

export function resolveMediaUrl(value?: string | null) {
  if (!value) {
    return null
  }

  if (/^https?:\/\//i.test(value)) {
    return value
  }

  if (value.startsWith('/api/media/')) {
    return `${API_BASE}${value}`
  }

  if (value.startsWith('media/')) {
    return `${API_BASE}/api/${value}`
  }

  return value
}

export type ModerationMode = 'Manual' | 'Automatic'
export type ChannelStatus = 'Draft' | 'Connected' | 'Disabled' | 'Error'
export type SourceKind = 'Reddit' | 'Web' | 'AiWebSearch' | 'Rss' | 'Telegram'
export type RedditListingKind = 'Hot' | 'New' | 'Rising' | 'Top'
export type FactCheckMode = 'Soft' | 'Medium' | 'Strict' | 'Custom'
export type RumorPolicy = 'Deny' | 'AllowWithLabel' | 'WhitelistedOnly' | 'AlwaysManual'
export type MediaGenerationMode = 'None' | 'UseSourceImage' | 'GeneratePoster' | 'TranslateMeme'
export type PublicationKind =
  | 'News'
  | 'BreakingNews'
  | 'Rumor'
  | 'Meme'
  | 'Digest'
  | 'Deal'
  | 'Trailer'
export type PostStatus =
  | 'CandidateFound'
  | 'WaitingFactCheck'
  | 'FactCheckFailed'
  | 'Duplicate'
  | 'GeneratingText'
  | 'GeneratingImage'
  | 'WaitingModeration'
  | 'NeedsRewrite'
  | 'Scheduled'
  | 'Published'
  | 'PublishFailed'
  | 'Rejected'

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

export interface WorkerStatus {
  enabled: boolean
  intervalMinutes: number
  maxPostsPerRun: number
}

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
  positioning: string
  systemPrompt: string
  styleGuide: string
  defaultModerationMode: ModerationMode
  dailyPostLimit: number
  dailyAiBudgetLimit?: number | null
  isEnabled: boolean
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
  lastCheckedAtUtc?: string | null
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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    headers: {
      'Content-Type': 'application/json',
      ...init?.headers,
    },
    ...init,
  })

  if (!response.ok) {
    const text = await response.text()
    throw new Error(text || `API error ${response.status}`)
  }

  if (response.status === 204) {
    return undefined as T
  }

  return response.json() as Promise<T>
}

export const api = {
  dashboard: (channelId?: string) =>
    request<Dashboard>(`/api/dashboard${channelId ? `?channelId=${channelId}` : ''}`),
  channels: () => request<ChannelListItem[]>('/api/channels'),
  channel: (id: string) => request<ChannelDetails>(`/api/channels/${id}`),
  saveChannel: (id: string, payload: Omit<ChannelDetails, 'id' | 'status'>) =>
    request<void>(`/api/channels/${id}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    }),
  setAutopilot: (id: string, enabled: boolean) =>
    request<ChannelDetails>(`/api/channels/${id}/autopilot`, {
      method: 'PUT',
      body: JSON.stringify({ enabled }),
    }),
  publicationTypes: (channelId: string) =>
    request<PublicationTypeItem[]>(`/api/channels/${channelId}/publication-types`),
  savePublicationType: (channelId: string, typeId: string, payload: Omit<PublicationTypeItem, 'id' | 'kind' | 'name' | 'description'>) =>
    request<void>(`/api/channels/${channelId}/publication-types/${typeId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    }),
  footerLinks: (channelId: string) =>
    request<FooterLinkItem[]>(`/api/channels/${channelId}/footer-links`),
  saveFooterLinks: (channelId: string, links: FooterLinkItem[]) =>
    request<void>(`/api/channels/${channelId}/footer-links`, {
      method: 'PUT',
      body: JSON.stringify({ links }),
    }),
  sources: (channelId: string) =>
    request<SourceItem[]>(`/api/channels/${channelId}/sources`),
  createSource: (
    channelId: string,
    payload: {
      name: string
      kind: SourceKind
      isEnabled: boolean
      checkEveryMinutes: number
      url?: string
      subreddit?: string
      redditListing: RedditListingKind
      minimumScore: number
      minimumComments: number
      whitelistKeywordsCsv?: string
      blacklistKeywordsCsv?: string
      allowedPublicationKindsCsv?: string
      allowNsfw: boolean
      allowRumors: boolean
    },
  ) =>
    request<{ id: string }>(`/api/channels/${channelId}/sources`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  posts: (status?: PostStatus, channelId?: string) => {
    const params = new URLSearchParams()
    if (status) {
      params.set('status', status)
    }
    if (channelId) {
      params.set('channelId', channelId)
    }

    const query = params.toString()
    return request<PostItem[]>(`/api/posts${query ? `?${query}` : ''}`)
  },
  generateDraftPost: (payload: GenerateDraftPostRequest) =>
    request<PostItem>('/api/posts/generate-draft', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  runPipeline: (
    channelId: string,
    options?: {
      publishNewPostsImmediately?: boolean
      maxPostsToCreate?: number
      ignoreSourceSchedule?: boolean
      bypassDailyLimit?: boolean
      publicationKind?: PublicationKind
    },
  ) => {
    const params = new URLSearchParams()
    if (options?.publishNewPostsImmediately) {
      params.set('publishNewPostsImmediately', 'true')
    }
    if (options?.maxPostsToCreate) {
      params.set('maxPostsToCreate', String(options.maxPostsToCreate))
    }
    if (options?.ignoreSourceSchedule) {
      params.set('ignoreSourceSchedule', 'true')
    }
    if (options?.bypassDailyLimit) {
      params.set('bypassDailyLimit', 'true')
    }
    if (options?.publicationKind) {
      params.set('publicationKind', options.publicationKind)
    }

    const query = params.toString()
    return request<PipelineRunResult>(`/api/pipeline/channels/${channelId}/run${query ? `?${query}` : ''}`, {
      method: 'POST',
    })
  },
  polzaStatus: () => request<AiAccountStatus>('/api/integrations/polza/status'),
  workerStatus: () => request<WorkerStatus>('/api/integrations/worker/status'),
  publishPost: (postId: string) =>
    request<PostItem>(`/api/posts/${postId}/publish`, { method: 'POST' }),
  updatePost: (
    postId: string,
    payload: {
      finalText: string
      scheduledForUtc?: string | null
    },
  ) =>
    request<void>(`/api/posts/${postId}`, {
      method: 'PUT',
      body: JSON.stringify(payload),
    }),
  rewritePost: (postId: string) =>
    request<PostItem>(`/api/posts/${postId}/rewrite`, { method: 'POST' }),
  regenerateImage: (postId: string) =>
    request<PostItem>(`/api/posts/${postId}/regenerate-image`, { method: 'POST' }),
  rejectPost: (postId: string, reason: string) =>
    request<void>(`/api/posts/${postId}/reject`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    }),
}
