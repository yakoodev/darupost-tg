import { useCallback, useEffect, useMemo, useState } from 'react'
import { Download, EyeOff, RotateCcw, Sparkles, ExternalLink } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { dateTime, kindLabels } from '../lib/format'
import { Badge, Button, Card, Empty, PageHead, Select, Stat, Switch } from '../components/ui'
import type { CandidateItem, CandidateList, PublicationKind } from '../lib/types'

const KINDS: PublicationKind[] = ['News', 'BreakingNews', 'Rumor', 'Trailer', 'Deal', 'Meme']
const REASON_LABELS: Record<string, string> = {
  post: 'пост создан',
  duplicate: 'дубль',
  factcheck: 'не прошёл фактчек',
  dismissed: 'скрыт',
  expired: 'устарел',
  orphan: 'источник удалён',
}

function relTime(iso: string) {
  const diff = Date.now() - new Date(iso).getTime()
  const m = Math.round(diff / 60000)
  if (m < 1) return 'только что'
  if (m < 60) return `${m} мин назад`
  const h = Math.round(m / 60)
  if (h < 48) return `${h} ч назад`
  return dateTime(iso)
}

export default function Today() {
  const { selectedChannelId, live } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canModerate = canOn(selectedChannelId, 'Moderator')
  const isAdmin = canOn(selectedChannelId, 'ChannelAdmin')

  const [data, setData] = useState<CandidateList | null>(null)
  const [hours, setHours] = useState(24)
  const [includeConsumed, setIncludeConsumed] = useState(false)
  const [sourceId, setSourceId] = useState('')
  const [kind, setKind] = useState<PublicationKind | ''>('')
  const [busy, setBusy] = useState('')
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    if (!selectedChannelId) return
    setLoading(true)
    try {
      setData(await api.candidates(selectedChannelId, { hours, includeConsumed, sourceId: sourceId || undefined }))
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка загрузки')
    } finally {
      setLoading(false)
    }
  }, [selectedChannelId, hours, includeConsumed, sourceId, toast])

  useEffect(() => { load() }, [load, live])

  async function act(name: string, fn: () => Promise<unknown>, okMsg: string) {
    setBusy(name)
    try {
      await fn()
      toast.success(okMsg)
      await load()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  async function ingestNow() {
    if (!selectedChannelId) return
    await act('ingest', async () => {
      const r = await api.ingestNow(selectedChannelId)
      const fresh = r.reduce((s, x) => s + x.newCandidates, 0)
      const errors = r.filter((x) => x.error).length
      toast.success(`Собрано: ${fresh} новых${errors ? `, ошибок: ${errors}` : ''}`)
    }, 'Сбор завершён')
  }

  async function generate(c: CandidateItem) {
    if (!selectedChannelId) return
    await act(`gen:${c.id}`, async () => {
      const r = await api.generateFromCandidate(selectedChannelId, c.id, kind || undefined)
      if (r.postsCreated > 0) return
      const why = r.duplicatesSkipped ? 'дубль' : r.factCheckFailed ? 'не прошёл фактчек' : r.warnings[0] ?? 'пост не создан'
      throw new Error(why)
    }, 'Черновик создан, смотри «Очередь»')
  }

  const errorSources = useMemo(() => (data?.sources ?? []).filter((s) => s.lastError), [data])

  return (
    <>
      <PageHead
        title="Сегодня"
        subtitle="Что собрали парсеры за день. Отсюда можно сделать пост вручную; вечером из этого же собирается дайджест"
        actions={
          <div className="row" style={{ gap: 8 }}>
            <Select value={hours} onChange={(e) => setHours(Number(e.target.value))}>
              <option value={6}>6 часов</option>
              <option value={12}>12 часов</option>
              <option value={24}>24 часа</option>
              <option value={48}>48 часов</option>
              <option value={168}>Неделя</option>
            </Select>
            {isAdmin && (
              <Button variant="primary" loading={busy === 'ingest'} onClick={ingestNow}>
                <Download size={15} /> Собрать сейчас
              </Button>
            )}
          </div>
        }
      />

      {!selectedChannelId && <Card><Empty>Канал не выбран.</Empty></Card>}

      {selectedChannelId && (
        <>
          <div className="grid cols-4">
            <Stat label="Собрано" value={data?.total ?? '—'} />
            <Stat label="Ожидают решения" value={data?.pending ?? '—'} accent />
            <Stat label="Источников" value={data ? `${data.sources.filter((s) => s.isEnabled).length} / ${data.sources.length}` : '—'} />
            <Stat label="С ошибкой" value={errorSources.length} />
          </div>

          <Card title="Источники" subtitle="Клик по источнику фильтрует список">
            <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
              <Button size="sm" variant={sourceId === '' ? 'primary' : 'ghost'} onClick={() => setSourceId('')}>Все</Button>
              {(data?.sources ?? []).map((s) => (
                <Button
                  key={s.sourceId}
                  size="sm"
                  variant={sourceId === s.sourceId ? 'primary' : 'ghost'}
                  onClick={() => setSourceId(s.sourceId)}
                  title={s.lastError ?? (s.lastCheckedAtUtc ? `Проверен ${relTime(s.lastCheckedAtUtc)}, найдено ${s.lastCollectedCount}` : 'Ещё не проверялся')}
                >
                  {!s.isEnabled && <EyeOff size={13} />}
                  {s.name}
                  <Badge tone={s.lastError ? 'red' : s.pending > 0 ? 'green' : ''}>{s.pending}/{s.total}</Badge>
                </Button>
              ))}
            </div>
            {errorSources.length > 0 && (
              <div style={{ marginTop: 10 }} className="faint">
                {errorSources.map((s) => (
                  <div key={s.sourceId} style={{ fontSize: 12 }}>
                    <Badge tone="red">{s.name}</Badge> {s.lastError}
                  </div>
                ))}
              </div>
            )}
          </Card>

          <Card
            title={`Инфоповоды${data ? ` (${data.items.length})` : ''}`}
            actions={
              <div className="row" style={{ gap: 10 }}>
                <Switch checked={includeConsumed} onChange={setIncludeConsumed} label="Показывать обработанные" />
                <Select value={kind} onChange={(e) => setKind(e.target.value as PublicationKind | '')}>
                  <option value="">Тип: авто</option>
                  {KINDS.map((k) => <option key={k} value={k}>{kindLabels[k] ?? k}</option>)}
                </Select>
              </div>
            }
          >
            {loading && !data && <Empty>Загрузка…</Empty>}
            {data && data.items.length === 0 && <Empty>Пока пусто. Нажми «Собрать сейчас» или подожди воркер.</Empty>}
            <div className="grid" style={{ gap: 10 }}>
              {(data?.items ?? []).map((c) => (
                <div key={c.id} className="queue-item" style={{ opacity: c.isConsumed ? 0.6 : 1 }}>
                  <div className="row" style={{ gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <Badge tone="blue">{c.sourceName}</Badge>
                    {c.isConsumed && <Badge tone="amber">{REASON_LABELS[c.consumedReason ?? ''] ?? c.consumedReason ?? 'обработан'}</Badge>}
                    {c.score != null && <span className="faint" style={{ fontSize: 12 }}>▲ {c.score}</span>}
                    {c.commentsCount != null && <span className="faint" style={{ fontSize: 12 }}>💬 {c.commentsCount}</span>}
                    {c.videoUrl && <Badge>видео</Badge>}
                    {c.author && <span className="faint" style={{ fontSize: 12 }}>@{c.author}</span>}
                    <span className="faint" style={{ fontSize: 11.5, marginLeft: 'auto' }} title={dateTime(c.foundAtUtc)}>{relTime(c.foundAtUtc)}</span>
                  </div>
                  <div style={{ fontWeight: 600, marginTop: 6 }}>
                    {c.url ? (
                      <a href={c.url} target="_blank" rel="noreferrer" style={{ color: 'inherit' }}>
                        {c.title} <ExternalLink size={12} style={{ verticalAlign: 'middle', opacity: 0.6 }} />
                      </a>
                    ) : c.title}
                  </div>
                  {c.summary && c.summary !== c.title && (
                    <div className="faint" style={{ fontSize: 13, marginTop: 4, whiteSpace: 'pre-wrap' }}>{c.summary}</div>
                  )}
                  {canModerate && (
                    <div className="row" style={{ gap: 8, marginTop: 8 }}>
                      <Button size="sm" variant="primary" loading={busy === `gen:${c.id}`} onClick={() => generate(c)}>
                        <Sparkles size={14} /> Сделать пост
                      </Button>
                      {!c.isConsumed ? (
                        <Button size="sm" variant="ghost" loading={busy === `dismiss:${c.id}`} onClick={() => act(`dismiss:${c.id}`, () => api.dismissCandidate(selectedChannelId, c.id), 'Скрыто')}>
                          <EyeOff size={14} /> Скрыть
                        </Button>
                      ) : (
                        <Button size="sm" variant="ghost" loading={busy === `restore:${c.id}`} onClick={() => act(`restore:${c.id}`, () => api.restoreCandidate(selectedChannelId, c.id), 'Возвращено')}>
                          <RotateCcw size={14} /> Вернуть
                        </Button>
                      )}
                    </div>
                  )}
                </div>
              ))}
            </div>
          </Card>
        </>
      )}
    </>
  )
}
