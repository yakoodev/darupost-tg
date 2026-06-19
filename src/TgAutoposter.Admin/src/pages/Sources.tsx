import { useCallback, useEffect, useState } from 'react'
import { Plus, Save, X } from 'lucide-react'
import { api } from '../lib/api'
import type { SourcePayload } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { dateTime } from '../lib/format'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, Textarea, TextInput } from '../components/ui'
import type { RedditListingKind, SourceItem, SourceKind } from '../lib/types'

const SOURCE_KINDS: SourceKind[] = ['Reddit', 'Web', 'AiWebSearch', 'Rss', 'Telegram']
const REDDIT_LISTINGS: RedditListingKind[] = ['Hot', 'New', 'Rising', 'Top']

const kindLabels: Record<SourceKind, string> = {
  Reddit: 'Reddit',
  Web: 'Веб-страница',
  AiWebSearch: 'AI-поиск',
  Rss: 'RSS',
  Telegram: 'Telegram',
}

function emptyForm(): SourcePayload {
  return {
    name: '',
    kind: 'Reddit',
    isEnabled: true,
    checkEveryMinutes: 60,
    url: '',
    subreddit: '',
    redditListing: 'Hot',
    minimumScore: 0,
    minimumComments: 0,
    whitelistKeywordsCsv: '',
    blacklistKeywordsCsv: '',
    allowedPublicationKindsCsv: '',
    allowNsfw: false,
    allowRumors: false,
  }
}

function blank(value?: string): string | undefined {
  const v = value?.trim()
  return v ? v : undefined
}

function truncate(value: string, max = 48): string {
  return value.length > max ? `${value.slice(0, max)}…` : value
}

export default function Sources() {
  const { selectedChannelId, live, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [items, setItems] = useState<SourceItem[]>([])
  const [editingId, setEditingId] = useState<string | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState<SourcePayload>(emptyForm)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    if (!selectedChannelId) {
      setItems([])
      return
    }
    const list = await api.sources(selectedChannelId)
    setItems(list)
  }, [selectedChannelId])

  useEffect(() => { load() }, [load, live])

  function startCreate() {
    setEditingId(null)
    setForm(emptyForm())
    setShowForm(true)
  }

  function startEdit(item: SourceItem) {
    if (!canEdit) return
    setEditingId(item.id)
    setForm({
      name: item.name,
      kind: item.kind,
      isEnabled: item.isEnabled,
      checkEveryMinutes: item.checkEveryMinutes,
      url: item.url ?? '',
      subreddit: item.subreddit ?? '',
      redditListing: item.redditListing,
      minimumScore: item.minimumScore,
      minimumComments: item.minimumComments,
      whitelistKeywordsCsv: '',
      blacklistKeywordsCsv: '',
      allowedPublicationKindsCsv: item.allowedPublicationKindsCsv ?? '',
      allowNsfw: false,
      allowRumors: false,
    })
    setShowForm(true)
  }

  function closeForm() {
    setShowForm(false)
    setEditingId(null)
    setForm(emptyForm())
  }

  function patch<K extends keyof SourcePayload>(key: K, value: SourcePayload[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  async function save() {
    if (!selectedChannelId) return
    setSaving(true)
    try {
      const payload: SourcePayload = {
        ...form,
        name: form.name.trim(),
        url: blank(form.url),
        subreddit: blank(form.subreddit),
        whitelistKeywordsCsv: blank(form.whitelistKeywordsCsv),
        blacklistKeywordsCsv: blank(form.blacklistKeywordsCsv),
        allowedPublicationKindsCsv: blank(form.allowedPublicationKindsCsv),
      }
      if (editingId) {
        await api.updateSource(selectedChannelId, editingId, payload)
      } else {
        await api.createSource(selectedChannelId, payload)
      }
      toast.success('Источник сохранён')
      closeForm()
      refresh()
      await load()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSaving(false)
    }
  }

  if (!selectedChannelId) {
    return (
      <>
        <PageHead title="Источники" />
        <Card><Empty>Выберите канал, чтобы управлять источниками.</Empty></Card>
      </>
    )
  }

  const isReddit = form.kind === 'Reddit'
  const isUrlKind = form.kind === 'Rss' || form.kind === 'Web' || form.kind === 'AiWebSearch'

  return (
    <>
      <PageHead
        title="Источники"
        subtitle={`${items.length} источников`}
        actions={canEdit ? (
          <Button variant="primary" onClick={startCreate}><Plus size={15} /> Добавить</Button>
        ) : undefined}
      />

      {!canEdit && <Card><Badge>только просмотр</Badge></Card>}

      <Card title="Список источников">
        {items.length === 0 ? (
          <Empty>Источников пока нет.</Empty>
        ) : (
          <div className="table-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th>Название</th>
                  <th>Тип</th>
                  <th>URL / subreddit</th>
                  <th>Порог</th>
                  <th>Частота</th>
                  <th>Типы</th>
                  <th>Последняя проверка</th>
                  <th>Статус</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr
                    key={item.id}
                    onClick={() => startEdit(item)}
                    style={canEdit ? { cursor: 'pointer' } : undefined}
                  >
                    <td>{item.name}</td>
                    <td><Badge>{kindLabels[item.kind]}</Badge></td>
                    <td className="mono">{truncate(item.subreddit || item.url || '—')}</td>
                    <td>{item.minimumScore} / {item.minimumComments}</td>
                    <td>{item.checkEveryMinutes} мин</td>
                    <td>{item.allowedPublicationKindsCsv || '—'}</td>
                    <td>{dateTime(item.lastCheckedAtUtc)}</td>
                    <td>
                      {item.isEnabled
                        ? <Badge tone="green">включён</Badge>
                        : <Badge>выключен</Badge>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {canEdit && showForm && (
        <Card
          title={editingId ? 'Редактирование источника' : 'Новый источник'}
          actions={<Button variant="ghost" size="sm" onClick={closeForm}><X size={15} /> Закрыть</Button>}
        >
          <Field label="Название">
            <TextInput value={form.name} onChange={(e) => patch('name', e.target.value)} />
          </Field>

          <Field label="Тип">
            <Select value={form.kind} onChange={(e) => patch('kind', e.target.value as SourceKind)}>
              {SOURCE_KINDS.map((k) => <option key={k} value={k}>{kindLabels[k]}</option>)}
            </Select>
          </Field>

          {isReddit && (
            <>
              <Field label="Subreddit">
                <TextInput value={form.subreddit ?? ''} onChange={(e) => patch('subreddit', e.target.value)} />
              </Field>
              <Field label="Листинг">
                <Select value={form.redditListing} onChange={(e) => patch('redditListing', e.target.value as RedditListingKind)}>
                  {REDDIT_LISTINGS.map((l) => <option key={l} value={l}>{l}</option>)}
                </Select>
              </Field>
              <Field label="Минимум очков">
                <TextInput type="number" value={form.minimumScore} onChange={(e) => patch('minimumScore', Number(e.target.value))} />
              </Field>
              <Field label="Минимум комментариев">
                <TextInput type="number" value={form.minimumComments} onChange={(e) => patch('minimumComments', Number(e.target.value))} />
              </Field>
            </>
          )}

          {isUrlKind && (
            <Field
              label={form.kind === 'AiWebSearch' ? 'Поисковый запрос' : 'URL'}
              hint={form.kind === 'AiWebSearch' ? 'Текст запроса для AI-поиска' : undefined}
            >
              <TextInput value={form.url ?? ''} onChange={(e) => patch('url', e.target.value)} />
            </Field>
          )}

          <Field label="Частота проверки (мин)">
            <TextInput type="number" value={form.checkEveryMinutes} onChange={(e) => patch('checkEveryMinutes', Number(e.target.value))} />
          </Field>

          <Field label="Типы публикаций" hint="News,Digest,…">
            <TextInput value={form.allowedPublicationKindsCsv ?? ''} onChange={(e) => patch('allowedPublicationKindsCsv', e.target.value)} />
          </Field>

          <Field label="Белый список ключевых слов">
            <Textarea value={form.whitelistKeywordsCsv ?? ''} onChange={(e) => patch('whitelistKeywordsCsv', e.target.value)} />
          </Field>

          <Field label="Чёрный список ключевых слов">
            <Textarea value={form.blacklistKeywordsCsv ?? ''} onChange={(e) => patch('blacklistKeywordsCsv', e.target.value)} />
          </Field>

          <Field>
            <Switch checked={form.allowNsfw} onChange={(v) => patch('allowNsfw', v)} label="Разрешить NSFW" />
          </Field>
          <Field>
            <Switch checked={form.allowRumors} onChange={(v) => patch('allowRumors', v)} label="Разрешить слухи" />
          </Field>
          <Field>
            <Switch checked={form.isEnabled} onChange={(v) => patch('isEnabled', v)} label="Источник включён" />
          </Field>

          <div className="row" style={{ marginTop: 8 }}>
            <Button variant="primary" loading={saving} onClick={save}>
              <Save size={15} /> Сохранить
            </Button>
          </div>
        </Card>
      )}
    </>
  )
}
