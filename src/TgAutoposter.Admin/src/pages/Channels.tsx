import { useEffect, useState } from 'react'
import { Plus, Save, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, TextInput } from '../components/ui'
import type { ChannelDetails, FooterLinkItem, ModerationMode } from '../lib/types'

interface CreateForm {
  name: string
  telegramUsername: string
  timeZone: string
  language: string
  dailyPostLimit: number
}

const emptyCreate: CreateForm = { name: '', telegramUsername: '', timeZone: 'Europe/Moscow', language: 'ru', dailyPostLimit: 6 }

export default function Channels() {
  const { selectedChannelId, refresh } = useAppData()
  const { user, canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [form, setForm] = useState<ChannelDetails | null>(null)
  const [links, setLinks] = useState<FooterLinkItem[]>([])
  const [saving, setSaving] = useState('')

  const [showCreate, setShowCreate] = useState(false)
  const [createForm, setCreateForm] = useState<CreateForm>(emptyCreate)

  useEffect(() => {
    if (!selectedChannelId) {
      setForm(null)
      setLinks([])
      return
    }
    let active = true
    Promise.all([api.channel(selectedChannelId), api.footerLinks(selectedChannelId)])
      .then(([details, footer]) => {
        if (!active) return
        setForm(details)
        setLinks(footer)
      })
      .catch((e) => toast.error(e instanceof Error ? e.message : 'Ошибка'))
    return () => { active = false }
  }, [selectedChannelId, toast])

  function patch<K extends keyof ChannelDetails>(key: K, value: ChannelDetails[K]) {
    setForm((cur) => (cur ? { ...cur, [key]: value } : cur))
  }

  async function save() {
    if (!form) return
    setSaving('channel')
    try {
      await api.saveChannel(form.id, {
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
      toast.success('Канал сохранён')
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSaving('')
    }
  }

  async function saveFooter() {
    if (!selectedChannelId) return
    setSaving('footer')
    try {
      await api.saveFooterLinks(selectedChannelId, links)
      toast.success('Футер сохранён')
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSaving('')
    }
  }

  async function create() {
    setSaving('create')
    try {
      await api.createChannel({
        name: createForm.name,
        telegramUsername: createForm.telegramUsername,
        telegramChatId: null,
        timeZone: createForm.timeZone,
        language: createForm.language,
        positioning: '',
        systemPrompt: '',
        styleGuide: '',
        defaultModerationMode: 'Manual',
        dailyPostLimit: createForm.dailyPostLimit,
        dailyAiBudgetLimit: null,
        isEnabled: true,
      })
      toast.success('Канал создан')
      setShowCreate(false)
      setCreateForm(emptyCreate)
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSaving('')
    }
  }

  function addLink() {
    setLinks((cur) => [...cur, { label: '', url: '', sortOrder: cur.length, isEnabled: true }])
  }

  function patchLink(index: number, partial: Partial<FooterLinkItem>) {
    setLinks((cur) => cur.map((l, i) => (i === index ? { ...l, ...partial } : l)))
  }

  function removeLink(index: number) {
    setLinks((cur) => cur.filter((_, i) => i !== index))
  }

  const createActions = user?.isGlobalOwner ? (
    <Button variant="primary" onClick={() => setShowCreate((v) => !v)}>
      <Plus size={16} /> Новый канал
    </Button>
  ) : undefined

  return (
    <>
      <PageHead title="Настройки канала" subtitle="Подключение, лимиты и футер" actions={createActions} />

      {showCreate && (
        <Card className="pad-lg" title="Новый канал">
          <div className="grid cols-2">
            <Field label="Название">
              <TextInput value={createForm.name} onChange={(e) => setCreateForm({ ...createForm, name: e.target.value })} />
            </Field>
            <Field label="Telegram-username">
              <TextInput value={createForm.telegramUsername} onChange={(e) => setCreateForm({ ...createForm, telegramUsername: e.target.value })} />
            </Field>
            <Field label="Часовой пояс">
              <TextInput value={createForm.timeZone} onChange={(e) => setCreateForm({ ...createForm, timeZone: e.target.value })} />
            </Field>
            <Field label="Язык">
              <TextInput value={createForm.language} onChange={(e) => setCreateForm({ ...createForm, language: e.target.value })} />
            </Field>
            <Field label="Лимит постов в день">
              <TextInput type="number" value={createForm.dailyPostLimit} onChange={(e) => setCreateForm({ ...createForm, dailyPostLimit: Number(e.target.value) })} />
            </Field>
          </div>
          <div className="row" style={{ marginTop: 12 }}>
            <Button variant="primary" loading={saving === 'create'} disabled={!createForm.name} onClick={create}>
              <Plus size={15} /> Создать
            </Button>
            <Button variant="ghost" onClick={() => setShowCreate(false)}>Отмена</Button>
          </div>
        </Card>
      )}

      {!selectedChannelId && <Card><Empty>Канал не выбран.</Empty></Card>}

      {selectedChannelId && form && (
        <>
          {!canEdit && <Card><Badge tone="amber">Только просмотр</Badge></Card>}

          <Card
            className="pad-lg"
            title="Канал"
            subtitle={form.status}
            actions={
              <Button variant="primary" loading={saving === 'channel'} disabled={!canEdit} onClick={save}>
                <Save size={16} /> Сохранить
              </Button>
            }
          >
            <div className="grid cols-2">
              <Field label="Название">
                <TextInput value={form.name} disabled={!canEdit} onChange={(e) => patch('name', e.target.value)} />
              </Field>
              <Field label="Telegram-username">
                <TextInput value={form.telegramUsername ?? ''} disabled={!canEdit} onChange={(e) => patch('telegramUsername', e.target.value)} />
              </Field>
              <Field label="Telegram chat id">
                <TextInput value={form.telegramChatId ?? ''} disabled={!canEdit} onChange={(e) => patch('telegramChatId', e.target.value)} />
              </Field>
              <Field label="Часовой пояс">
                <TextInput value={form.timeZone} disabled={!canEdit} onChange={(e) => patch('timeZone', e.target.value)} />
              </Field>
              <Field label="Язык">
                <TextInput value={form.language} disabled={!canEdit} onChange={(e) => patch('language', e.target.value)} />
              </Field>
              <Field label="Режим модерации">
                <Select value={form.defaultModerationMode} disabled={!canEdit} onChange={(e) => patch('defaultModerationMode', e.target.value as ModerationMode)}>
                  <option value="Manual">Ручная</option>
                  <option value="Automatic">Автоматическая</option>
                </Select>
              </Field>
              <Field label="Лимит постов в день">
                <TextInput type="number" value={form.dailyPostLimit} disabled={!canEdit} onChange={(e) => patch('dailyPostLimit', Number(e.target.value))} />
              </Field>
              <Field label="Дневной AI-бюджет (₽)" hint="Пусто — без лимита">
                <TextInput
                  type="number"
                  value={form.dailyAiBudgetLimit ?? ''}
                  disabled={!canEdit}
                  onChange={(e) => patch('dailyAiBudgetLimit', e.target.value === '' ? null : Number(e.target.value))}
                />
              </Field>
            </div>
            <div className="divider" />
            <Switch checked={form.isEnabled} onChange={(v) => canEdit && patch('isEnabled', v)} label="Канал включён" />
          </Card>

          <Card
            className="pad-lg"
            title="Футер канала"
            subtitle="Ссылки, добавляемые в конец публикаций"
            actions={
              <div className="row">
                <Button variant="ghost" size="sm" disabled={!canEdit} onClick={addLink}><Plus size={15} /> Ссылка</Button>
                <Button variant="primary" size="sm" loading={saving === 'footer'} disabled={!canEdit} onClick={saveFooter}>
                  <Save size={15} /> Сохранить футер
                </Button>
              </div>
            }
          >
            {links.length === 0 && <Empty>Ссылок пока нет.</Empty>}
            <div className="grid" style={{ gap: 10 }}>
              {links.map((link, i) => (
                <div key={link.id ?? i} className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
                  <Field label="Текст">
                    <TextInput value={link.label} disabled={!canEdit} onChange={(e) => patchLink(i, { label: e.target.value })} />
                  </Field>
                  <Field label="Ссылка">
                    <TextInput value={link.url} disabled={!canEdit} onChange={(e) => patchLink(i, { url: e.target.value })} />
                  </Field>
                  <div style={{ paddingBottom: 6 }}>
                    <Switch checked={link.isEnabled} onChange={(v) => canEdit && patchLink(i, { isEnabled: v })} label="Вкл" />
                  </div>
                  <Button variant="danger" size="sm" disabled={!canEdit} onClick={() => removeLink(i)}><Trash2 size={15} /></Button>
                </div>
              ))}
            </div>
          </Card>
        </>
      )}
    </>
  )
}
