import {
  Activity,
  Bot,
  CheckCircle2,
  Clock3,
  Coins,
  History,
  Link2,
  Palette,
  Play,
  Power,
  Plus,
  Save,
  Send,
  Settings2,
  ShieldCheck,
  Sparkles,
  Trash2,
  X,
} from 'lucide-react'
import * as signalR from '@microsoft/signalr'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import './App.css'
import { API_BASE, api } from './api'
import type {
  AiAccountStatus,
  ChannelDetails,
  ChannelListItem,
  Dashboard,
  FooterLinkItem,
  GenerateDraftPostRequest,
  PipelineRunResult,
  PostItem,
  PublicationTypeItem,
  PublicationKind,
  SourceItem,
  WorkerStatus,
} from './api'

type TabKey = 'queue' | 'channels' | 'branding' | 'sources' | 'history' | 'costs'
type LiveState = 'connecting' | 'connected' | 'disconnected'

const emptyDashboard: Dashboard = {
  channels: 0,
  enabledSources: 0,
  queueToday: 0,
  waitingModeration: 0,
  publishedToday: 0,
  publishedMonth: 0,
  rejected: 0,
  duplicatesFound: 0,
  publishErrors: 0,
  aiSpendToday: 0,
  aiSpendMonth: 0,
  averagePublishedPostCost: 0,
  providerSpendToday: 0,
  providerSpendMonth: 0,
}

const contentKinds: Array<{ kind: PublicationKind; label: string; detail: string }> = [
  { kind: 'News', label: 'Новость', detail: 'свежие источники' },
  { kind: 'Rumor', label: 'Слух', detail: 'утечки и инсайды' },
  { kind: 'Meme', label: 'Мем', detail: 'Reddit image + RU' },
  { kind: 'Digest', label: 'Дайджест', detail: 'несколько тем' },
  { kind: 'Deal', label: 'Раздача', detail: 'скидки и freebie' },
  { kind: 'Trailer', label: 'Трейлер', detail: 'с видео' },
]

const tabItems: Array<{ value: TabKey; label: string; icon: ReactNode }> = [
  { value: 'queue', label: 'Модерация', icon: <Clock3 size={17} /> },
  { value: 'channels', label: 'Канал', icon: <Settings2 size={17} /> },
  { value: 'branding', label: 'Оформление', icon: <Palette size={17} /> },
  { value: 'sources', label: 'Источники', icon: <Activity size={17} /> },
  { value: 'history', label: 'История', icon: <History size={17} /> },
  { value: 'costs', label: 'Расходы', icon: <Coins size={17} /> },
]

function groupIndexFromPath() {
  const match = window.location.pathname.match(/^\/group\/(\d+)\/?$/)
  if (!match) {
    return 1
  }

  return Math.max(1, Number(match[1]) || 1)
}

function groupPathForChannel(channels: ChannelListItem[], channelId: string) {
  const index = Math.max(0, channels.findIndex((channel) => channel.id === channelId))
  return `/group/${index + 1}/`
}

function syncGroupPath(channels: ChannelListItem[], channelId: string, replace = true) {
  if (!channelId || channels.length === 0) {
    return
  }

  const nextPath = groupPathForChannel(channels, channelId)
  if (window.location.pathname === nextPath) {
    return
  }

  const nextUrl = `${nextPath}${window.location.search}${window.location.hash}`
  if (replace) {
    window.history.replaceState(null, '', nextUrl)
    return
  }

  window.history.pushState(null, '', nextUrl)
}

function App() {
  const [activeTab, setActiveTab] = useState<TabKey>('queue')
  const [quickKind, setQuickKind] = useState<PublicationKind>('News')
  const [dashboard, setDashboard] = useState<Dashboard>(emptyDashboard)
  const [channels, setChannels] = useState<ChannelListItem[]>([])
  const [selectedChannelId, setSelectedChannelId] = useState<string>('')
  const [channelDetails, setChannelDetails] = useState<ChannelDetails | null>(null)
  const [sources, setSources] = useState<SourceItem[]>([])
  const [publicationTypes, setPublicationTypes] = useState<PublicationTypeItem[]>([])
  const [footerLinks, setFooterLinks] = useState<FooterLinkItem[]>([])
  const [posts, setPosts] = useState<PostItem[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const [notice, setNotice] = useState<string>('Готово')
  const [liveState, setLiveState] = useState<LiveState>('connecting')
  const [lastRun, setLastRun] = useState<PipelineRunResult | null>(null)
  const [polzaStatus, setPolzaStatus] = useState<AiAccountStatus | null>(null)
  const [workerStatus, setWorkerStatus] = useState<WorkerStatus | null>(null)
  const refreshTimerRef = useRef<number | null>(null)

  const selectedChannel = useMemo(
    () => channels.find((channel) => channel.id === selectedChannelId),
    [channels, selectedChannelId],
  )
  const generationEnabled = channelDetails?.isEnabled ?? selectedChannel?.isEnabled ?? false
  const autopublishEnabled = publicationTypes.some((type) => type.moderationMode === 'Automatic')
  const enabledSourceCount = sources.filter((source) => source.isEnabled).length
  const posterTypes = publicationTypes.filter((type) => type.mediaMode === 'GeneratePoster' && type.isEnabled).length
  const localizedMemeTypes = publicationTypes.filter((type) => type.mediaMode === 'TranslateMeme' && type.isEnabled).length
  const activeKind = contentKinds.find((item) => item.kind === quickKind)
  const currentPath = selectedChannelId ? groupPathForChannel(channels, selectedChannelId) : '/group/—/'
  const trailerSources = sources.filter((source) => source.isEnabled && source.allowedPublicationKindsCsv?.includes('Trailer')).length
  const memeSources = sources.filter((source) => source.isEnabled && source.allowedPublicationKindsCsv?.includes('Meme')).length

  const loadAll = useCallback(async (silent = false, forcedChannelId?: string) => {
    if (!silent) {
      setIsLoading(true)
    }
    try {
      const [channelData, statusData, workerData] = await Promise.all([
        api.channels(),
        api.polzaStatus(),
        api.workerStatus(),
      ])
      setChannels(channelData)
      setPolzaStatus(statusData)
      setWorkerStatus(workerData)

      const groupIndex = groupIndexFromPath()
      const routedChannelId = channelData[groupIndex - 1]?.id
      const nextChannelId = forcedChannelId || selectedChannelId || routedChannelId || channelData[0]?.id || ''
      setSelectedChannelId(nextChannelId)
      syncGroupPath(channelData, nextChannelId, !forcedChannelId)

      if (nextChannelId) {
        const [dashboardData, postData, details, sourceData, publicationTypeData, footerLinkData] = await Promise.all([
          api.dashboard(nextChannelId),
          api.posts(undefined, nextChannelId),
          api.channel(nextChannelId),
          api.sources(nextChannelId),
          api.publicationTypes(nextChannelId),
          api.footerLinks(nextChannelId),
        ])
        setDashboard(dashboardData)
        setPosts(postData)
        setChannelDetails(details)
        setSources(sourceData)
        setPublicationTypes(publicationTypeData)
        setFooterLinks(footerLinkData)
      } else {
        setDashboard(emptyDashboard)
        setPosts([])
        setChannelDetails(null)
        setSources([])
        setPublicationTypes([])
        setFooterLinks([])
      }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка загрузки')
    } finally {
      if (!silent) {
        setIsLoading(false)
      }
    }
  }, [selectedChannelId])

  useEffect(() => {
    loadAll()
  }, [loadAll])

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE}/hubs/posts`)
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    function scheduleRefresh() {
      if (refreshTimerRef.current) {
        window.clearTimeout(refreshTimerRef.current)
      }

      refreshTimerRef.current = window.setTimeout(() => {
        loadAll(true)
      }, 300)
    }

    connection.on('stateChanged', scheduleRefresh)
    connection.onreconnecting(() => setLiveState('connecting'))
    connection.onreconnected(() => {
      setLiveState('connected')
      scheduleRefresh()
    })
    connection.onclose(() => setLiveState('disconnected'))

    connection
      .start()
      .then(() => setLiveState('connected'))
      .catch(() => setLiveState('disconnected'))

    return () => {
      if (refreshTimerRef.current) {
        window.clearTimeout(refreshTimerRef.current)
      }
      connection.stop()
    }
  }, [loadAll])

  function changeChannel(channelId: string) {
    setSelectedChannelId(channelId)
    setLastRun(null)
    syncGroupPath(channels, channelId, false)
    loadAll(false, channelId)
  }

  useEffect(() => {
    function handlePopState() {
      const channel = channels[groupIndexFromPath() - 1]
      if (channel) {
        setSelectedChannelId(channel.id)
        setLastRun(null)
        loadAll(false, channel.id)
      }
    }

    window.addEventListener('popstate', handlePopState)
    return () => window.removeEventListener('popstate', handlePopState)
  }, [channels, loadAll])

  const queuePosts = posts.filter((post) =>
    ['WaitingModeration', 'Scheduled', 'PublishFailed', 'NeedsRewrite'].includes(post.status),
  )
  const historyPosts = posts.filter((post) =>
    ['Published', 'Rejected', 'FactCheckFailed', 'Duplicate'].includes(post.status),
  )

  async function runPipeline() {
    if (!selectedChannelId) {
      return
    }

    setIsLoading(true)
    try {
      const result = await api.runPipeline(selectedChannelId, {
        publishNewPostsImmediately: true,
        maxPostsToCreate: 1,
        ignoreSourceSchedule: true,
        bypassDailyLimit: true,
        publicationKind: quickKind,
      })
      setLastRun(result)
      const warningSuffix = result.warnings.length > 0 ? `, предупреждений: ${result.warnings.length}` : ''
      setNotice(`${publicationKindLabel(quickKind)}: ${result.postsCreated} черновик(ов), ${result.candidatesCollected} кандидат(ов)${warningSuffix}`)
      await loadAll()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка запуска пайплайна')
    } finally {
      setIsLoading(false)
    }
  }

  async function toggleGeneration() {
    if (!channelDetails) {
      return
    }

    setIsLoading(true)
    try {
      await api.saveChannel(channelDetails.id, {
        name: channelDetails.name,
        telegramUsername: channelDetails.telegramUsername,
        telegramChatId: channelDetails.telegramChatId,
        timeZone: channelDetails.timeZone,
        language: channelDetails.language,
        positioning: channelDetails.positioning,
        systemPrompt: channelDetails.systemPrompt,
        styleGuide: channelDetails.styleGuide,
        defaultModerationMode: channelDetails.defaultModerationMode,
        dailyPostLimit: channelDetails.dailyPostLimit,
        dailyAiBudgetLimit: channelDetails.dailyAiBudgetLimit,
        isEnabled: !generationEnabled,
      })
      setNotice(!generationEnabled ? 'Генерация включена' : 'Генерация выключена')
      await loadAll()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка переключения генерации')
    } finally {
      setIsLoading(false)
    }
  }

  async function moderate(postId: string, action: 'publish' | 'rewrite' | 'reject' | 'image') {
    setIsLoading(true)
    try {
      if (action === 'publish') {
        await api.publishPost(postId)
      }
      if (action === 'rewrite') {
        await api.rewritePost(postId)
      }
      if (action === 'image') {
        await api.regenerateImage(postId)
      }
      if (action === 'reject') {
        await api.rejectPost(postId, 'Отклонено из админки')
      }

      setNotice(action === 'publish' ? 'Публикация отправлена' : 'Статус поста обновлён')
      await loadAll()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка действия')
    } finally {
      setIsLoading(false)
    }
  }

  async function savePost(postId: string, finalText: string, scheduledForUtc?: string | null) {
    setIsLoading(true)
    try {
      await api.updatePost(postId, { finalText, scheduledForUtc })
      setNotice('Пост сохранён')
      await loadAll()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка сохранения поста')
    } finally {
      setIsLoading(false)
    }
  }

  async function createDraft(payload: GenerateDraftPostRequest) {
    setIsLoading(true)
    try {
      const post = await api.generateDraftPost(payload)
      setNotice(`Черновик создан: ${post.sourceTitle}`)
      setActiveTab('queue')
      await loadAll()
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Ошибка генерации черновика')
    } finally {
      setIsLoading(false)
    }
  }

  return (
    <div className="app-shell">
      <aside className="project-rail">
        <div className="rail-brand">
          <Bot size={22} />
          <div>
            <strong>Daru Autoposter</strong>
            <span>Telegram projects</span>
          </div>
        </div>

        <div className="channel-switcher">
          <div className="rail-section-title">
            <span>Паблики</span>
            <strong>{channels.length}</strong>
          </div>
          <div className="channel-cabinet-list">
            {channels.map((channel, index) => (
              <button
                type="button"
                key={channel.id}
                className={channel.id === selectedChannelId ? 'channel-cabinet is-active' : 'channel-cabinet'}
                onClick={() => changeChannel(channel.id)}
              >
                <span className="project-code">/group/{index + 1}/</span>
                <strong>{channel.name}</strong>
                <small>{channel.telegramUsername ?? 'без username'}</small>
                <em>{channel.queueCount} очередь · {channel.sourcesCount} источн. · {channel.isEnabled ? 'on' : 'off'}</em>
              </button>
            ))}
          </div>
        </div>

        <div className="rail-footer">
          <span className={`live-pill live-${liveState}`}>
            <Activity size={14} />
            {liveState === 'connected' ? 'live' : liveState === 'connecting' ? 'sync' : 'offline'}
          </span>
          <span className="rail-cost">₽{dashboard.aiSpendToday.toFixed(2)} сегодня</span>
        </div>
      </aside>

      <main className="studio">
        <header className="project-hero">
          <div className="project-heading">
            <span className="project-path">{currentPath}</span>
            <h1>{selectedChannel?.name ?? 'канал не выбран'}</h1>
            <div className="project-meta">
              <span>{selectedChannel?.telegramUsername ?? 'Telegram не подключён'}</span>
              <span>{channelDetails?.timeZone ?? 'timezone не задан'}</span>
              <span>{channelDetails?.defaultModerationMode === 'Automatic' ? 'auto publish' : 'manual review'}</span>
            </div>
          </div>
          <div className="hero-actions">
            <button
              type="button"
              className={generationEnabled ? 'autopilot-button is-active' : 'autopilot-button'}
              onClick={toggleGeneration}
              disabled={!channelDetails || isLoading}
            >
              <Power size={17} />
              {generationEnabled ? 'Автоцикл вкл' : 'Канал на паузе'}
            </button>
            <span className={workerStatus?.enabled ? 'mode-pill is-live' : 'mode-pill'}>
              {workerStatus?.enabled ? `каждые ${workerStatus.intervalMinutes} мин` : 'только вручную'}
            </span>
            <span className={autopublishEnabled ? 'mode-pill is-warning' : 'mode-pill'}>
              {autopublishEnabled ? 'автопубликация' : 'через модерацию'}
            </span>
            <span className={isLoading ? 'status is-loading' : 'status'}>{notice}</span>
          </div>
        </header>

        <section className="command-center" aria-label="Управление генерацией">
          <div className="run-console">
            <div className="console-head">
              <div>
                <span>Запуск</span>
                <strong>{activeKind?.label}</strong>
              </div>
              <button type="button" className="primary-button launch-button" onClick={runPipeline} disabled={!selectedChannelId || isLoading}>
                <Play size={17} />
                Сгенерировать
              </button>
            </div>
            <div className="kind-switcher" role="group" aria-label="Тип публикации">
              {contentKinds.map((item) => (
                <button
                  type="button"
                  key={item.kind}
                  className={quickKind === item.kind ? 'kind-chip is-active' : 'kind-chip'}
                  onClick={() => setQuickKind(item.kind)}
                >
                  <strong>{item.label}</strong>
                  <span>{item.detail}</span>
                </button>
              ))}
            </div>
          </div>

          <div className="ops-board">
            <div className={workerStatus?.enabled ? 'ops-line' : 'ops-line is-muted'}>
              <span>Автозапуск</span>
              <strong>{workerStatus?.enabled ? `${workerStatus.intervalMinutes} мин · ${workerStatus.maxPostsPerRun} пост` : 'off'}</strong>
            </div>
            <div className={generationEnabled ? 'ops-line' : 'ops-line is-muted'}>
              <span>Канал</span>
              <strong>{generationEnabled ? 'в работе' : 'пауза'}</strong>
            </div>
            <div className="ops-line">
              <span>Источники</span>
              <strong>{enabledSourceCount}</strong>
            </div>
            <div className="ops-line">
              <span>Трейлеры</span>
              <strong>{trailerSources}</strong>
            </div>
            <div className="ops-line">
              <span>Мемы</span>
              <strong>{memeSources}</strong>
            </div>
            <div className="ops-line">
              <span>Картинки</span>
              <strong>{posterTypes + localizedMemeTypes}</strong>
            </div>
            <div className={polzaStatus?.error ? 'ops-line is-danger' : 'ops-line'}>
              <span>Polza</span>
              <strong>{polzaStatus?.balanceRub != null ? `₽${polzaStatus.balanceRub.toFixed(0)}` : polzaStatus?.enabled ? 'on' : 'off'}</strong>
            </div>
          </div>
        </section>

        <section className="signal-grid" aria-label="Сводка кабинета">
          <Metric label="Источники" value={dashboard.enabledSources} />
          <Metric label="Очередь сегодня" value={dashboard.queueToday} />
          <Metric label="На модерации" value={dashboard.waitingModeration} />
          <Metric label="Опубликовано сегодня" value={dashboard.publishedToday} />
          <Metric label="Расход сегодня" value={`₽${dashboard.aiSpendToday.toFixed(2)}`} />
          <Metric label="Расход месяц" value={`₽${dashboard.aiSpendMonth.toFixed(2)}`} />
          <Metric label="Polza факт сегодня" value={`₽${dashboard.providerSpendToday.toFixed(2)}`} />
        </section>

        {lastRun && (
          <section className="run-strip">
            <span>Источников: {lastRun.sourcesChecked}</span>
            <span>Кандидатов: {lastRun.candidatesCollected}</span>
            <span>Постов: {lastRun.postsCreated}</span>
            <span>Опубликовано: {lastRun.publishedThisRun}</span>
            <span>Ошибок публикации: {lastRun.publishFailed}</span>
            <span>Дублей: {lastRun.duplicatesSkipped}</span>
            <span>Фактчек провален: {lastRun.factCheckFailed}</span>
          </section>
        )}
        {lastRun?.warnings.length ? (
          <section className="warning-strip">
            {lastRun.warnings.slice(0, 4).map((warning) => (
              <span key={warning}>{warning}</span>
            ))}
          </section>
        ) : null}

        <nav className="workspace-tabs" aria-label="Разделы кабинета">
          {tabItems.map((item) => (
            <TabButton
              key={item.value}
              icon={item.icon}
              label={item.label}
              value={item.value}
              active={activeTab}
              onClick={setActiveTab}
            />
          ))}
        </nav>

        <div className="workspace-body">
          {activeTab === 'queue' && (
            <QueueView
              posts={queuePosts}
              selectedChannelId={selectedChannelId}
              isBusy={isLoading}
              onCreateDraft={createDraft}
              onSave={savePost}
              onModerate={moderate}
            />
          )}
          {activeTab === 'channels' && channelDetails && (
            <ChannelView channel={channelDetails} onSaved={loadAll} />
          )}
          {activeTab === 'branding' && selectedChannelId && (
            <BrandingView
              channelId={selectedChannelId}
              publicationTypes={publicationTypes}
              footerLinks={footerLinks}
              onSaved={loadAll}
            />
          )}
          {activeTab === 'sources' && selectedChannelId && (
            <SourcesView channelId={selectedChannelId} sources={sources} onCreated={loadAll} />
          )}
          {activeTab === 'history' && <HistoryView posts={historyPosts} />}
          {activeTab === 'costs' && <CostsView dashboard={dashboard} posts={posts} />}
        </div>
      </main>
    </div>
  )
}

function TabButton({
  icon,
  label,
  value,
  active,
  onClick,
}: {
  icon: ReactNode
  label: string
  value: TabKey
  active: TabKey
  onClick: (value: TabKey) => void
}) {
  return (
    <button
      type="button"
      className={active === value ? 'workspace-tab is-active' : 'workspace-tab'}
      onClick={() => onClick(value)}
    >
      {icon}
      {label}
    </button>
  )
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="metric">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  )
}

function QueueView({
  posts,
  selectedChannelId,
  isBusy,
  onCreateDraft,
  onSave,
  onModerate,
}: {
  posts: PostItem[]
  selectedChannelId: string
  isBusy: boolean
  onCreateDraft: (payload: GenerateDraftPostRequest) => Promise<void>
  onSave: (postId: string, finalText: string, scheduledForUtc?: string | null) => Promise<void>
  onModerate: (postId: string, action: 'publish' | 'rewrite' | 'reject' | 'image') => Promise<void>
}) {
  const [publicationKind, setPublicationKind] = useState<PublicationKind>('News')
  const [sourceTitle, setSourceTitle] = useState('')
  const [sourceUrl, setSourceUrl] = useState('')
  const [summary, setSummary] = useState('')
  const [selectedPostId, setSelectedPostId] = useState(posts[0]?.id ?? '')

  useEffect(() => {
    if (!posts.some((post) => post.id === selectedPostId)) {
      setSelectedPostId(posts[0]?.id ?? '')
    }
  }, [posts, selectedPostId])

  const selectedPost = posts.find((post) => post.id === selectedPostId) ?? posts[0]

  async function submit(event: FormEvent) {
    event.preventDefault()
    await onCreateDraft({
      channelId: selectedChannelId,
      publicationKind,
      sourceTitle,
      sourceUrl: sourceUrl.trim() || null,
      summary,
      scheduledForUtc: null,
    })
    setSourceTitle('')
    setSourceUrl('')
    setSummary('')
  }

  return (
    <div className="queue-workbench">
      <section className="panel queue-list-panel">
        <PanelHeader icon={<ShieldCheck size={18} />} title="Модерация" />
        <form className="draft-composer compact" onSubmit={submit}>
          <div className="draft-controls compact">
            <Field label="Тип">
              <select
                value={publicationKind}
                onChange={(event) => setPublicationKind(event.target.value as PublicationKind)}
              >
                <option value="News">Новость</option>
                <option value="Rumor">Слух</option>
                <option value="Digest">Дайджест</option>
                <option value="Meme">Мем</option>
                <option value="Deal">Раздача</option>
                <option value="Trailer">Трейлер</option>
              </select>
            </Field>
            <Field label="Тема">
              <input value={sourceTitle} onChange={(event) => setSourceTitle(event.target.value)} />
            </Field>
          </div>
          <Field label="Ссылка">
            <input value={sourceUrl} onChange={(event) => setSourceUrl(event.target.value)} />
          </Field>
          <Field label="Контекст">
            <textarea className="compact-textarea" value={summary} onChange={(event) => setSummary(event.target.value)} />
          </Field>
          <button
            type="submit"
            className="primary-button"
            disabled={!selectedChannelId || isBusy || !sourceTitle.trim() || !summary.trim()}
          >
            <Sparkles size={17} />
            Черновик
          </button>
        </form>
        <div className="queue-list">
          {posts.length === 0 && <EmptyState text="Очередь пуста" />}
          {posts.map((post) => (
            <button
              type="button"
              key={post.id}
              className={selectedPost?.id === post.id ? 'queue-card is-active' : 'queue-card'}
              onClick={() => setSelectedPostId(post.id)}
            >
              <span className={`badge status-${post.status.toLowerCase()}`}>{statusLabel(post.status)}</span>
              <strong>{post.sourceTitle}</strong>
              <span>{post.publicationKind} · {formatDate(post.scheduledForUtc ?? post.createdAtUtc)}</span>
            </button>
          ))}
        </div>
      </section>

      <section className="panel review-panel">
        {selectedPost ? (
          <QueuePostDetail
            post={selectedPost}
            isBusy={isBusy}
            onSave={onSave}
            onModerate={onModerate}
          />
        ) : (
          <EmptyState text="Нет поста для модерации" />
        )}
      </section>
    </div>
  )
}

function QueuePostDetail({
  post,
  isBusy,
  onSave,
  onModerate,
}: {
  post: PostItem
  isBusy: boolean
  onSave: (postId: string, finalText: string, scheduledForUtc?: string | null) => Promise<void>
  onModerate: (postId: string, action: 'publish' | 'rewrite' | 'reject' | 'image') => Promise<void>
}) {
  const [text, setText] = useState(post.finalText ?? '')

  useEffect(() => {
    setText(post.finalText ?? '')
  }, [post.id, post.finalText])

  const isDirty = text.trim() !== (post.finalText ?? '').trim()
  const canSave = !isBusy && text.trim().length > 0 && isDirty

  async function publish() {
    if (isDirty) {
      await onSave(post.id, text, post.scheduledForUtc)
    }

    await onModerate(post.id, 'publish')
  }

  return (
    <article className="review-detail">
      <div className="review-main">
        <div className="row-heading">
          <span className={`badge status-${post.status.toLowerCase()}`}>{statusLabel(post.status)}</span>
          <span>{post.publicationKind}</span>
          <span>{formatDate(post.scheduledForUtc)}</span>
          {post.model && <span>{post.model}</span>}
        </div>
        <h2>{post.sourceTitle}</h2>
        {post.sourceUrl && <a className="source-link" href={post.sourceUrl} target="_blank" rel="noreferrer">{post.sourceUrl}</a>}
        {post.videoUrl && (
          <a className="video-link" href={post.videoUrl} target="_blank" rel="noreferrer">
            <Play size={15} />
            Видео / трейлер
          </a>
        )}
        <textarea
          className="post-editor"
          value={text}
          onChange={(event) => setText(event.target.value)}
        />
        {post.imagePath && (
          <a className="image-preview" href={post.imagePath} target="_blank" rel="noreferrer">
            <img src={post.imagePath} alt="" />
          </a>
        )}
        <div className="checks">
          <span>Фактчек: {post.factCheckStatus}</span>
          <span>Дубли: {post.deduplicationStatus}</span>
        </div>
      </div>
      <div className="review-actions">
        <button type="button" className="secondary-button" onClick={() => onSave(post.id, text, post.scheduledForUtc)} disabled={!canSave}>
          <Save size={17} />
          Сохранить
        </button>
        <button type="button" className="primary-button" onClick={publish} disabled={isBusy || !text.trim()}>
          <Send size={17} />
          Публикуем
        </button>
        <button type="button" className="secondary-button" onClick={() => onModerate(post.id, 'image')} disabled={isBusy}>
          <Sparkles size={17} />
          Картинка
        </button>
        <button type="button" className="secondary-button" onClick={() => onModerate(post.id, 'rewrite')} disabled={isBusy}>
          <Sparkles size={17} />
          Переписать
        </button>
        <button type="button" className="secondary-button danger" onClick={() => onModerate(post.id, 'reject')} disabled={isBusy}>
          <Trash2 size={17} />
          Не публикуем
        </button>
      </div>
    </article>
  )
}

function ChannelView({ channel, onSaved }: { channel: ChannelDetails; onSaved: () => Promise<void> }) {
  const [form, setForm] = useState(channel)
  const [saving, setSaving] = useState(false)

  useEffect(() => setForm(channel), [channel])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaving(true)
    try {
      await api.saveChannel(channel.id, {
        name: form.name,
        telegramUsername: form.telegramUsername,
        telegramChatId: form.telegramChatId,
        timeZone: form.timeZone,
        language: form.language,
        positioning: form.positioning,
        systemPrompt: form.systemPrompt,
        styleGuide: form.styleGuide,
        defaultModerationMode: form.defaultModerationMode,
        dailyPostLimit: form.dailyPostLimit,
        dailyAiBudgetLimit: form.dailyAiBudgetLimit,
        isEnabled: form.isEnabled,
      })
      await onSaved()
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="panel">
      <PanelHeader icon={<Settings2 size={18} />} title="Настройки канала" />
      <form className="form-grid" onSubmit={submit}>
        <Field label="Название">
          <input value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} />
        </Field>
        <Field label="Telegram username">
          <input value={form.telegramUsername ?? ''} onChange={(event) => setForm({ ...form, telegramUsername: event.target.value })} />
        </Field>
        <Field label="Telegram chat id">
          <input value={form.telegramChatId ?? ''} onChange={(event) => setForm({ ...form, telegramChatId: event.target.value })} />
        </Field>
        <Field label="Часовой пояс">
          <input value={form.timeZone} onChange={(event) => setForm({ ...form, timeZone: event.target.value })} />
        </Field>
        <Field label="Лимит в день">
          <input
            type="number"
            min="1"
            value={form.dailyPostLimit}
            onChange={(event) => setForm({ ...form, dailyPostLimit: Number(event.target.value) })}
          />
        </Field>
        <Field label="Автопубликация">
          <select
            value={form.defaultModerationMode}
            onChange={(event) => setForm({ ...form, defaultModerationMode: event.target.value as ChannelDetails['defaultModerationMode'] })}
          >
            <option value="Manual">Через модерацию</option>
            <option value="Automatic">Без модерации</option>
          </select>
        </Field>
        <Field label="Позиционирование" wide>
          <textarea value={form.positioning} onChange={(event) => setForm({ ...form, positioning: event.target.value })} />
        </Field>
        <Field label="Системный промпт" wide>
          <textarea value={form.systemPrompt} onChange={(event) => setForm({ ...form, systemPrompt: event.target.value })} />
        </Field>
        <Field label="Стиль" wide>
          <textarea value={form.styleGuide} onChange={(event) => setForm({ ...form, styleGuide: event.target.value })} />
        </Field>
        <label className="checkline">
          <input
            type="checkbox"
            checked={form.isEnabled}
            onChange={(event) => setForm({ ...form, isEnabled: event.target.checked })}
          />
          Генерация канала включена
        </label>
        <button type="submit" className="primary-button form-submit" disabled={saving}>
          <Save size={17} />
          Сохранить
        </button>
      </form>
    </section>
  )
}

function BrandingView({
  channelId,
  publicationTypes,
  footerLinks,
  onSaved,
}: {
  channelId: string
  publicationTypes: PublicationTypeItem[]
  footerLinks: FooterLinkItem[]
  onSaved: () => Promise<void>
}) {
  const [types, setTypes] = useState(publicationTypes)
  const [links, setLinks] = useState<FooterLinkItem[]>(footerLinks)
  const [saving, setSaving] = useState(false)

  useEffect(() => setTypes(publicationTypes), [publicationTypes])
  useEffect(() => setLinks(footerLinks), [footerLinks])

  function updateType(id: string, patch: Partial<PublicationTypeItem>) {
    setTypes((current) => current.map((type) => (type.id === id ? { ...type, ...patch } : type)))
  }

  function updateLink(index: number, patch: Partial<FooterLinkItem>) {
    setLinks((current) => current.map((link, itemIndex) => (itemIndex === index ? { ...link, ...patch } : link)))
  }

  function addLink() {
    setLinks((current) => [
      ...current,
      {
        label: '',
        url: '',
        sortOrder: (current.length + 1) * 10,
        isEnabled: true,
        publicationKindsCsv: null,
      },
    ])
  }

  function removeLink(index: number) {
    setLinks((current) => current.filter((_, itemIndex) => itemIndex !== index))
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setSaving(true)

    try {
      const normalizedLinks = links
        .map((link, index) => ({
          ...link,
          label: link.label.trim(),
          url: link.url.trim(),
          sortOrder: (index + 1) * 10,
          publicationKindsCsv: link.publicationKindsCsv || null,
        }))
        .filter((link) => link.label || link.url)

      await Promise.all([
        ...types.map((type) =>
          api.savePublicationType(channelId, type.id, {
            isEnabled: type.isEnabled,
            priority: type.priority,
            moderationMode: type.moderationMode,
            factCheckMode: type.factCheckMode,
            rumorPolicy: type.rumorPolicy,
            maxTextLength: type.maxTextLength,
            mediaMode: type.mediaMode,
            systemPrompt: type.systemPrompt,
            headerTemplate: type.headerTemplate?.trim() || null,
            footerTemplate: type.footerTemplate?.trim() || null,
          }),
        ),
        api.saveFooterLinks(channelId, normalizedLinks),
      ])

      await onSaved()
    } finally {
      setSaving(false)
    }
  }

  return (
    <form className="settings-stack" onSubmit={submit}>
      <section className="panel">
        <PanelHeader icon={<Palette size={18} />} title="Текст публикаций" />
        <div className="publication-settings">
          {types.map((type) => (
            <article className="publication-type-card" key={type.id}>
              <div className="publication-type-head">
                <div>
                  <h3>{type.name}</h3>
                  <span>{publicationKindLabel(type.kind)}</span>
                </div>
                <label className="checkline compact">
                  <input
                    type="checkbox"
                    checked={type.isEnabled}
                    onChange={(event) => updateType(type.id, { isEnabled: event.target.checked })}
                  />
                  Вкл
                </label>
              </div>
              <div className="type-options-grid">
                <Field label="Хедер">
                  <input
                    value={type.headerTemplate ?? ''}
                    onChange={(event) => updateType(type.id, { headerTemplate: event.target.value })}
                  />
                </Field>
                <Field label="Лимит">
                  <input
                    type="number"
                    min="280"
                    max="4096"
                    value={type.maxTextLength}
                    onChange={(event) => updateType(type.id, { maxTextLength: Number(event.target.value) })}
                  />
                </Field>
                <Field label="Режим">
                  <select
                    value={type.moderationMode}
                    onChange={(event) => updateType(type.id, { moderationMode: event.target.value as PublicationTypeItem['moderationMode'] })}
                  >
                    <option value="Manual">Ручной</option>
                    <option value="Automatic">Авто</option>
                  </select>
                </Field>
                <Field label="Фактчек">
                  <select
                    value={type.factCheckMode}
                    onChange={(event) => updateType(type.id, { factCheckMode: event.target.value as PublicationTypeItem['factCheckMode'] })}
                  >
                    <option value="Soft">Мягкий</option>
                    <option value="Medium">Средний</option>
                    <option value="Strict">Строгий</option>
                    <option value="Custom">Свой</option>
                  </select>
                </Field>
                <Field label="Медиа">
                  <select
                    value={type.mediaMode}
                    onChange={(event) => updateType(type.id, { mediaMode: event.target.value as PublicationTypeItem['mediaMode'] })}
                  >
                    <option value="None">Нет</option>
                    <option value="UseSourceImage">Из источника</option>
                    <option value="GeneratePoster">Генерировать</option>
                    <option value="TranslateMeme">Мем</option>
                  </select>
                </Field>
                <Field label="Приоритет">
                  <input
                    type="number"
                    value={type.priority}
                    onChange={(event) => updateType(type.id, { priority: Number(event.target.value) })}
                  />
                </Field>
                <Field label="Футер типа" wide>
                  <textarea
                    className="compact-textarea"
                    value={type.footerTemplate ?? ''}
                    onChange={(event) => updateType(type.id, { footerTemplate: event.target.value })}
                  />
                </Field>
                <Field label="Промпт типа" wide>
                  <textarea
                    value={type.systemPrompt}
                    onChange={(event) => updateType(type.id, { systemPrompt: event.target.value })}
                  />
                </Field>
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="panel">
        <PanelHeader icon={<Link2 size={18} />} title="Ссылки футера" />
        <div className="footer-links-editor">
          {links.map((link, index) => (
            <div className="footer-link-row" key={`${link.id ?? 'new'}-${index}`}>
              <input
                value={link.label}
                onChange={(event) => updateLink(index, { label: event.target.value })}
                aria-label="Название ссылки"
              />
              <input
                value={link.url}
                onChange={(event) => updateLink(index, { url: event.target.value })}
                aria-label="URL ссылки"
              />
              <select
                value={link.publicationKindsCsv ?? ''}
                onChange={(event) => updateLink(index, { publicationKindsCsv: event.target.value || null })}
                aria-label="Типы публикаций"
              >
                <option value="">Все</option>
                <option value="News,BreakingNews">Новости</option>
                <option value="Digest">Дайджест</option>
                <option value="Deal">Раздачи</option>
                <option value="Trailer">Трейлеры</option>
                <option value="Rumor">Слухи</option>
                <option value="Meme">Мемы</option>
              </select>
              <label className="checkline compact">
                <input
                  type="checkbox"
                  checked={link.isEnabled}
                  onChange={(event) => updateLink(index, { isEnabled: event.target.checked })}
                />
                Вкл
              </label>
              <button type="button" className="icon-button danger" onClick={() => removeLink(index)} title="Удалить">
                <X size={17} />
              </button>
            </div>
          ))}
          <div className="settings-actions">
            <button type="button" className="secondary-button" onClick={addLink}>
              <Plus size={17} />
              Добавить
            </button>
            <button type="submit" className="primary-button" disabled={saving}>
              <Save size={17} />
              Сохранить
            </button>
          </div>
        </div>
      </section>
    </form>
  )
}

function SourcesView({
  channelId,
  sources,
  onCreated,
}: {
  channelId: string
  sources: SourceItem[]
  onCreated: () => Promise<void>
}) {
  const [kind, setKind] = useState<SourceItem['kind']>('AiWebSearch')
  const [sourceName, setSourceName] = useState('Polza Web Search')
  const [sourceUrl, setSourceUrl] = useState('fresh video game news today release patch gaming industry PC console Nintendo PlayStation Xbox Steam')
  const [subreddit, setSubreddit] = useState('gaming')
  const [minimumScore, setMinimumScore] = useState(50)
  const [allowedKinds, setAllowedKinds] = useState('News,Digest,Deal')

  async function submit(event: FormEvent) {
    event.preventDefault()
    const isReddit = kind === 'Reddit'
    await api.createSource(channelId, {
      name: sourceName.trim() || (isReddit ? `r/${subreddit}` : kind),
      kind,
      isEnabled: true,
      checkEveryMinutes: kind === 'AiWebSearch' ? 45 : 60,
      url: isReddit ? undefined : sourceUrl,
      subreddit: isReddit ? subreddit : undefined,
      redditListing: 'Hot',
      minimumScore: isReddit ? minimumScore : 0,
      minimumComments: isReddit ? 10 : 0,
      allowedPublicationKindsCsv: allowedKinds,
      allowNsfw: false,
      allowRumors: false,
    })
    await onCreated()
  }

  return (
    <section className="panel">
      <PanelHeader icon={<Activity size={18} />} title="Источники" />
      <form className="inline-form" onSubmit={submit}>
        <select value={kind} onChange={(event) => setKind(event.target.value as SourceItem['kind'])}>
          <option value="AiWebSearch">Polza web</option>
          <option value="Rss">RSS</option>
          <option value="Reddit">Reddit</option>
        </select>
        <input value={sourceName} onChange={(event) => setSourceName(event.target.value)} aria-label="Название источника" />
        {kind === 'Reddit' ? (
          <>
            <input value={subreddit} onChange={(event) => setSubreddit(event.target.value)} aria-label="Subreddit" />
            <input
              type="number"
              min="0"
              value={minimumScore}
              onChange={(event) => setMinimumScore(Number(event.target.value))}
              aria-label="Минимальный score"
            />
          </>
        ) : (
          <input value={sourceUrl} onChange={(event) => setSourceUrl(event.target.value)} aria-label="URL или search prompt" />
        )}
        <select value={allowedKinds} onChange={(event) => setAllowedKinds(event.target.value)} aria-label="Типы контента">
          <option value="News,Digest,Deal">Новости + дайджест + раздачи</option>
          <option value="News,Digest">Новости + дайджест</option>
          <option value="Rumor">Слухи</option>
          <option value="Meme">Мемы</option>
          <option value="Deal">Раздачи</option>
          <option value="Trailer">Трейлеры с видео</option>
        </select>
        <button type="submit" className="primary-button">
          <Save size={17} />
          Добавить
        </button>
      </form>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Название</th>
              <th>Тип</th>
              <th>URL / prompt</th>
              <th>Порог</th>
              <th>Частота</th>
              <th>Типы</th>
              <th>Последняя проверка</th>
              <th>Статус</th>
            </tr>
          </thead>
          <tbody>
            {sources.map((source) => (
              <tr key={source.id}>
                <td>{source.name}</td>
                <td>{source.kind}</td>
                <td>{source.url ?? source.subreddit ?? '—'}</td>
                <td>{source.minimumScore} score / {source.minimumComments} comments</td>
                <td>{source.checkEveryMinutes} мин</td>
                <td>{source.allowedPublicationKindsCsv ?? 'Все'}</td>
                <td>{formatDate(source.lastCheckedAtUtc)}</td>
                <td>{source.isEnabled ? 'включён' : 'выключен'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function HistoryView({ posts }: { posts: PostItem[] }) {
  return (
    <section className="panel">
      <PanelHeader icon={<History size={18} />} title="История постов" />
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Дата</th>
              <th>Канал</th>
              <th>Тип</th>
              <th>Статус</th>
              <th>Заголовок</th>
              <th>Картинка</th>
              <th>Видео</th>
              <th>Ссылка</th>
            </tr>
          </thead>
          <tbody>
            {posts.map((post) => (
              <tr key={post.id}>
                <td>{formatDate(post.publishedAtUtc ?? post.createdAtUtc)}</td>
                <td>{post.channelName}</td>
                <td>{post.publicationKind}</td>
                <td>{statusLabel(post.status)}</td>
                <td>{post.sourceTitle}</td>
                <td>{post.imagePath ? <a href={post.imagePath}>Открыть</a> : '—'}</td>
                <td>{post.videoUrl ? <a href={post.videoUrl}>Открыть</a> : '—'}</td>
                <td>{post.telegramPostUrl ? <a href={post.telegramPostUrl}>Открыть</a> : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function CostsView({ dashboard, posts }: { dashboard: Dashboard; posts: PostItem[] }) {
  const paidPosts = posts.filter((post) => post.costAmount)
  return (
    <section className="panel">
      <PanelHeader icon={<Coins size={18} />} title="Расходы AI" />
      <div className="cost-grid">
        <Metric label="Сегодня" value={`₽${dashboard.aiSpendToday.toFixed(2)}`} />
        <Metric label="Месяц" value={`₽${dashboard.aiSpendMonth.toFixed(2)}`} />
        <Metric label="Polza сегодня" value={`₽${dashboard.providerSpendToday.toFixed(2)}`} />
        <Metric label="Polza месяц" value={`₽${dashboard.providerSpendMonth.toFixed(2)}`} />
        <Metric label="Средний пост" value={`₽${dashboard.averagePublishedPostCost.toFixed(2)}`} />
        <Metric label="Записей с ценой" value={paidPosts.length} />
      </div>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Пост</th>
              <th>Тип</th>
              <th>Статус</th>
              <th>Стоимость</th>
            </tr>
          </thead>
          <tbody>
            {paidPosts.map((post) => (
              <tr key={post.id}>
                <td>{post.sourceTitle}</td>
                <td>{post.publicationKind}</td>
                <td>{statusLabel(post.status)}</td>
                <td>{post.costAmount?.toFixed(4)} {post.costCurrency}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function Field({
  label,
  children,
  wide = false,
}: {
  label: string
  children: ReactNode
  wide?: boolean
}) {
  return (
    <label className={wide ? 'field is-wide' : 'field'}>
      <span>{label}</span>
      {children}
    </label>
  )
}

function PanelHeader({ icon, title }: { icon: ReactNode; title: string }) {
  return (
    <div className="panel-header">
      <div>
        {icon}
        <h2>{title}</h2>
      </div>
      <CheckCircle2 size={18} />
    </div>
  )
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state">{text}</div>
}

function publicationKindLabel(kind: PublicationKind) {
  const labels: Record<PublicationKind, string> = {
    News: 'новость',
    BreakingNews: 'срочно',
    Rumor: 'слух',
    Meme: 'мем',
    Digest: 'дайджест',
    Deal: 'раздача',
    Trailer: 'трейлер',
  }

  return labels[kind] ?? kind
}

function statusLabel(status: string) {
  const labels: Record<string, string> = {
    WaitingModeration: 'модерация',
    Scheduled: 'запланирован',
    Published: 'опубликован',
    PublishFailed: 'ошибка',
    Rejected: 'отклонён',
    NeedsRewrite: 'переделать',
    FactCheckFailed: 'фактчек',
    Duplicate: 'дубль',
  }

  return labels[status] ?? status
}

function formatDate(value?: string | null) {
  if (!value) {
    return '—'
  }

  return new Intl.DateTimeFormat('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}

export default App
