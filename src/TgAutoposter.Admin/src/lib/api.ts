import type {
  AiAccountStatus, ChannelDetails, ChannelListItem, ChannelMode, ChannelRoleType, CurrentUser, Dashboard,
  FooterLinkItem, GenerateDraftPostRequest, LoginResponse, PipelineRunResult, PostItem, PostStatus,
  PublicationTypeItem, RedditListingKind, ScheduleWindowItem, SourceItem, SourceKind, UserListItem,
  WorkerStatus,
} from './types'

declare global {
  interface Window { __API_BASE__?: string }
}

// Runtime override (window.__API_BASE__ from public/config.js) → build-time env → localhost default.
export const API_BASE =
  (typeof window !== 'undefined' && window.__API_BASE__) ||
  import.meta.env.VITE_API_URL ||
  'http://localhost:5000'

const TOKEN_KEY = 'tg.auth.token'
export const tokenStore = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (t: string) => localStorage.setItem(TOKEN_KEY, t),
  clear: () => localStorage.removeItem(TOKEN_KEY),
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

let onUnauthorized: (() => void) | null = null
export function setUnauthorizedHandler(fn: () => void) { onUnauthorized = fn }

export function resolveMediaUrl(value?: string | null) {
  if (!value) return null
  if (/^https?:\/\//i.test(value)) return value
  if (value.startsWith('/api/media/')) return `${API_BASE}${value}`
  if (value.startsWith('media/')) return `${API_BASE}/api/${value}`
  return value
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = tokenStore.get()
  const headers: Record<string, string> = { 'Content-Type': 'application/json', ...(init?.headers as Record<string, string>) }
  if (token) headers.Authorization = `Bearer ${token}`

  const response = await fetch(`${API_BASE}${path}`, { ...init, headers })

  if (response.status === 401) {
    tokenStore.clear()
    onUnauthorized?.()
    throw new ApiError(401, 'Сессия истекла. Войдите снова.')
  }

  if (!response.ok) {
    let message = `Ошибка API (${response.status})`
    const text = await response.text()
    if (text) {
      try {
        const parsed = JSON.parse(text)
        message = parsed.error || parsed.title || parsed.detail || text
      } catch { message = text }
    } else if (response.status === 403) {
      message = 'Недостаточно прав для этого действия.'
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T
  const ct = response.headers.get('content-type')
  return ct?.includes('application/json') ? (response.json() as Promise<T>) : (undefined as T)
}

export interface SourcePayload {
  name: string; kind: SourceKind; isEnabled: boolean; checkEveryMinutes: number
  url?: string; subreddit?: string; redditListing: RedditListingKind
  minimumScore: number; minimumComments: number
  whitelistKeywordsCsv?: string; blacklistKeywordsCsv?: string; allowedPublicationKindsCsv?: string
  allowNsfw: boolean; allowRumors: boolean
}

export interface RunPipelineOptions {
  publishNewPostsImmediately?: boolean; maxPostsToCreate?: number
  ignoreSourceSchedule?: boolean; bypassDailyLimit?: boolean; publicationKind?: string
}

export const api = {
  // auth
  login: (email: string, password: string) =>
    request<LoginResponse>('/api/auth/login', { method: 'POST', body: JSON.stringify({ email, password }) }),
  me: () => request<CurrentUser>('/api/auth/me'),

  // dashboard
  dashboard: (channelId?: string) =>
    request<Dashboard>(`/api/dashboard${channelId ? `?channelId=${channelId}` : ''}`),

  // channels
  channels: () => request<ChannelListItem[]>('/api/channels'),
  channel: (id: string) => request<ChannelDetails>(`/api/channels/${id}`),
  createChannel: (payload: Omit<ChannelDetails, 'id' | 'status'>) =>
    request<{ id: string }>('/api/channels', { method: 'POST', body: JSON.stringify(payload) }),
  saveChannel: (id: string, payload: Omit<ChannelDetails, 'id' | 'status'>) =>
    request<void>(`/api/channels/${id}`, { method: 'PUT', body: JSON.stringify(payload) }),
  setAutopilot: (id: string, enabled: boolean) =>
    request<ChannelDetails>(`/api/channels/${id}/autopilot`, { method: 'PUT', body: JSON.stringify({ enabled }) }),
  setMode: (id: string, mode: ChannelMode) =>
    request<void>(`/api/channels/${id}/mode`, { method: 'PUT', body: JSON.stringify({ mode }) }),

  // publication types
  publicationTypes: (channelId: string) =>
    request<PublicationTypeItem[]>(`/api/channels/${channelId}/publication-types`),
  savePublicationType: (channelId: string, typeId: string, payload: Omit<PublicationTypeItem, 'id' | 'kind' | 'name' | 'description'>) =>
    request<void>(`/api/channels/${channelId}/publication-types/${typeId}`, { method: 'PUT', body: JSON.stringify(payload) }),

  // footer links
  footerLinks: (channelId: string) => request<FooterLinkItem[]>(`/api/channels/${channelId}/footer-links`),
  saveFooterLinks: (channelId: string, links: FooterLinkItem[]) =>
    request<void>(`/api/channels/${channelId}/footer-links`, { method: 'PUT', body: JSON.stringify({ links }) }),

  // schedule
  schedule: (channelId: string) => request<ScheduleWindowItem[]>(`/api/channels/${channelId}/schedule`),
  saveSchedule: (channelId: string, windows: ScheduleWindowItem[]) =>
    request<void>(`/api/channels/${channelId}/schedule`, { method: 'PUT', body: JSON.stringify({ windows }) }),

  // sources
  sources: (channelId: string) => request<SourceItem[]>(`/api/channels/${channelId}/sources`),
  createSource: (channelId: string, payload: SourcePayload) =>
    request<{ id: string }>(`/api/channels/${channelId}/sources`, { method: 'POST', body: JSON.stringify(payload) }),
  updateSource: (channelId: string, sourceId: string, payload: SourcePayload) =>
    request<void>(`/api/channels/${channelId}/sources/${sourceId}`, { method: 'PUT', body: JSON.stringify(payload) }),

  // posts
  posts: (status?: PostStatus, channelId?: string) => {
    const p = new URLSearchParams()
    if (status) p.set('status', status)
    if (channelId) p.set('channelId', channelId)
    const q = p.toString()
    return request<PostItem[]>(`/api/posts${q ? `?${q}` : ''}`)
  },
  generateDraftPost: (payload: GenerateDraftPostRequest) =>
    request<PostItem>('/api/posts/generate-draft', { method: 'POST', body: JSON.stringify(payload) }),
  publishPost: (postId: string) => request<PostItem>(`/api/posts/${postId}/publish`, { method: 'POST' }),
  updatePost: (postId: string, payload: { finalText: string; scheduledForUtc?: string | null }) =>
    request<void>(`/api/posts/${postId}`, { method: 'PUT', body: JSON.stringify(payload) }),
  rewritePost: (postId: string) => request<PostItem>(`/api/posts/${postId}/rewrite`, { method: 'POST' }),
  regenerateImage: (postId: string) => request<PostItem>(`/api/posts/${postId}/regenerate-image`, { method: 'POST' }),
  rejectPost: (postId: string, reason: string) =>
    request<void>(`/api/posts/${postId}/reject`, { method: 'POST', body: JSON.stringify({ reason }) }),

  // pipeline
  runPipeline: (channelId: string, options?: RunPipelineOptions) => {
    const p = new URLSearchParams()
    if (options?.publishNewPostsImmediately) p.set('publishNewPostsImmediately', 'true')
    if (options?.maxPostsToCreate) p.set('maxPostsToCreate', String(options.maxPostsToCreate))
    if (options?.ignoreSourceSchedule) p.set('ignoreSourceSchedule', 'true')
    if (options?.bypassDailyLimit) p.set('bypassDailyLimit', 'true')
    if (options?.publicationKind) p.set('publicationKind', options.publicationKind)
    const q = p.toString()
    return request<PipelineRunResult>(`/api/pipeline/channels/${channelId}/run${q ? `?${q}` : ''}`, { method: 'POST' })
  },

  // integrations
  polzaStatus: () => request<AiAccountStatus>('/api/integrations/polza/status'),
  workerStatus: () => request<WorkerStatus>('/api/integrations/worker/status'),

  // users
  users: () => request<UserListItem[]>('/api/users'),
  createUser: (payload: { displayName: string; email?: string; password?: string; telegramUsername?: string; isGlobalOwner: boolean }) =>
    request<{ id: string }>('/api/users', { method: 'POST', body: JSON.stringify(payload) }),
  updateUser: (id: string, payload: { displayName: string; email?: string; telegramUsername?: string; isEnabled: boolean; isGlobalOwner: boolean; newPassword?: string }) =>
    request<void>(`/api/users/${id}`, { method: 'PUT', body: JSON.stringify(payload) }),
  assignRole: (id: string, channelId: string, role: ChannelRoleType) =>
    request<void>(`/api/users/${id}/roles`, { method: 'POST', body: JSON.stringify({ channelId, role }) }),
  removeRole: (id: string, roleId: string) =>
    request<void>(`/api/users/${id}/roles/${roleId}`, { method: 'DELETE' }),
}
