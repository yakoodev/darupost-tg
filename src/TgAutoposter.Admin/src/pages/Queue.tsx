import { useCallback, useEffect, useState } from 'react'
import { Image as ImageIcon, RefreshCw, Save, Send, X } from 'lucide-react'
import { api, resolveMediaUrl } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { dateTime, kindLabels, statusLabels, statusTone } from '../lib/format'
import { Badge, Button, Card, Empty, Field, PageHead, Textarea, TextInput } from '../components/ui'
import type { PostItem, PostStatus } from '../lib/types'

const QUEUE_STATUSES: PostStatus[] = ['WaitingModeration', 'NeedsRewrite', 'Scheduled', 'PublishFailed']

export default function Queue() {
  const { selectedChannelId, live } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canModerate = canOn(selectedChannelId, 'Moderator')

  const [posts, setPosts] = useState<PostItem[]>([])
  const [selectedId, setSelectedId] = useState<string>('')
  const [draft, setDraft] = useState('')
  const [scheduledFor, setScheduledFor] = useState('')
  const [busy, setBusy] = useState('')

  const load = useCallback(async () => {
    if (!selectedChannelId) return
    const all = await api.posts(undefined, selectedChannelId)
    const queue = all.filter((p) => QUEUE_STATUSES.includes(p.status))
    setPosts(queue)
    setSelectedId((cur) => (queue.some((p) => p.id === cur) ? cur : queue[0]?.id ?? ''))
  }, [selectedChannelId])

  useEffect(() => { load() }, [load, live])

  const selected = posts.find((p) => p.id === selectedId)
  useEffect(() => {
    setDraft(selected?.finalText ?? '')
    setScheduledFor(selected?.scheduledForUtc ? selected.scheduledForUtc.slice(0, 16) : '')
  }, [selectedId, selected?.finalText, selected?.scheduledForUtc])

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

  const mediaUrl = resolveMediaUrl(selected?.imagePath)

  return (
    <>
      <PageHead title="Очередь и модерация" subtitle={`${posts.length} в работе`} />
      {!canModerate && <Card><Empty>У вас нет прав модерации в этом канале.</Empty></Card>}
      {canModerate && (
        <div className="split">
          <Card title="На модерации">
            {posts.length === 0 && <Empty>Очередь пуста.</Empty>}
            <div className="grid" style={{ gap: 10 }}>
              {posts.map((p) => (
                <div key={p.id} className={`queue-item ${p.id === selectedId ? 'active' : ''}`} onClick={() => setSelectedId(p.id)}>
                  <div className="row" style={{ gap: 6, alignItems: 'center' }}>
                    <Badge tone={statusTone(p.status)}>{statusLabels[p.status]}</Badge>
                    <Badge>{kindLabels[p.publicationKind] ?? p.publicationKind}</Badge>
                    <span className="faint" style={{ fontSize: 11.5, marginLeft: 'auto' }}>{dateTime(p.createdAtUtc)}</span>
                  </div>
                  <div className="t">{p.sourceTitle}</div>
                </div>
              ))}
            </div>
          </Card>

          {selected ? (
            <Card
              title={kindLabels[selected.publicationKind] ?? selected.publicationKind}
              subtitle={selected.model ?? undefined}
              actions={<Badge tone={statusTone(selected.status)}>{statusLabels[selected.status]}</Badge>}
            >
              {selected.sourceUrl && <a className="link mono" href={selected.sourceUrl} target="_blank" rel="noreferrer" style={{ fontSize: 12, wordBreak: 'break-all' }}>{selected.sourceUrl}</a>}

              <Field label="Текст поста">
                <Textarea value={draft} onChange={(e) => setDraft(e.target.value)} style={{ minHeight: 200 }} />
              </Field>

              {mediaUrl && <img className="post-media" src={mediaUrl} alt="превью" />}

              <div className="row" style={{ marginTop: 12, fontSize: 12.5 }}>
                <span>Фактчек: <Badge tone={selected.factCheckStatus === 'Passed' ? 'green' : selected.factCheckStatus === 'Failed' ? 'red' : 'amber'}>{selected.factCheckStatus}</Badge></span>
                <span>Дубли: <Badge tone={selected.deduplicationStatus === 'Unique' ? 'green' : 'amber'}>{selected.deduplicationStatus}</Badge></span>
              </div>

              <Field label="Запланировать на">
                <TextInput type="datetime-local" value={scheduledFor} onChange={(e) => setScheduledFor(e.target.value)} />
              </Field>

              <div className="row" style={{ marginTop: 8 }}>
                <Button variant="ghost" size="sm" loading={busy === 'save'}
                  onClick={() => act('save', () => api.updatePost(selected.id, { finalText: draft, scheduledForUtc: scheduledFor ? new Date(scheduledFor).toISOString() : null }), 'Сохранено')}>
                  <Save size={15} /> Сохранить
                </Button>
                <Button variant="success" size="sm" loading={busy === 'publish'}
                  onClick={() => act('publish', () => api.publishPost(selected.id), 'Опубликовано')}>
                  <Send size={15} /> Опубликовать
                </Button>
                <Button size="sm" loading={busy === 'rewrite'}
                  onClick={() => act('rewrite', () => api.rewritePost(selected.id), 'Переписано')}>
                  <RefreshCw size={15} /> Переписать
                </Button>
                <Button size="sm" loading={busy === 'image'}
                  onClick={() => act('image', () => api.regenerateImage(selected.id), 'Картинка обновлена')}>
                  <ImageIcon size={15} /> Картинка
                </Button>
                <Button variant="danger" size="sm" loading={busy === 'reject'}
                  onClick={() => act('reject', () => api.rejectPost(selected.id, 'Отклонено модератором'), 'Отклонено')}>
                  <X size={15} /> Отклонить
                </Button>
              </div>
            </Card>
          ) : (
            <Card><Empty>Выберите пост слева.</Empty></Card>
          )}
        </div>
      )}
    </>
  )
}
