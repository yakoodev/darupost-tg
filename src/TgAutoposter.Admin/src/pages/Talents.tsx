import { useCallback, useEffect, useState } from 'react'
import { Plus, Save, Trash2, Upload, RefreshCw, X } from 'lucide-react'
import { api } from '../lib/api'
import type { TalentPayload } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, Textarea, TextInput } from '../components/ui'
import type { TalentItem } from '../lib/types'

function emptyForm(): TalentPayload {
  return {
    name: '', agency: '', group: '', aliasesCsv: '', priority: 2,
    youTube: '', twitter: '', telegram: '', trackYouTube: false, trackTwitter: false, isActive: true, notes: '',
  }
}

const PRIORITY_LABELS: Record<number, string> = { 1: 'топ', 2: 'обычный', 3: 'нишевый' }

export default function Talents() {
  const { selectedChannelId, live, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [items, setItems] = useState<TalentItem[]>([])
  const [filter, setFilter] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState<TalentPayload>(emptyForm)
  const [importText, setImportText] = useState('')
  const [showImport, setShowImport] = useState(false)
  const [busy, setBusy] = useState('')

  const load = useCallback(async () => {
    if (!selectedChannelId) { setItems([]); return }
    setItems(await api.talents(selectedChannelId))
  }, [selectedChannelId])

  useEffect(() => { load() }, [load, live])

  function startCreate() { setEditingId(null); setForm(emptyForm()); setShowForm(true) }
  function startEdit(t: TalentItem) {
    if (!canEdit) return
    setEditingId(t.id)
    setForm({
      name: t.name, agency: t.agency ?? '', group: t.group ?? '', aliasesCsv: t.aliasesCsv ?? '', priority: t.priority,
      youTube: t.youTube ?? '', twitter: t.twitter ?? '', telegram: t.telegram ?? '',
      trackYouTube: t.trackYouTube, trackTwitter: t.trackTwitter, isActive: t.isActive, notes: t.notes ?? '',
    })
    setShowForm(true)
  }
  function patch<K extends keyof TalentPayload>(key: K, value: TalentPayload[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  async function run(name: string, fn: () => Promise<unknown>, ok?: string) {
    setBusy(name)
    try {
      await fn()
      if (ok) toast.success(ok)
      await load()
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  async function save() {
    if (!selectedChannelId) return
    await run('save', async () => {
      if (editingId) await api.updateTalent(selectedChannelId, editingId, form)
      else await api.createTalent(selectedChannelId, form)
      setShowForm(false)
      setEditingId(null)
    }, 'Сохранено')
  }

  async function remove(t: TalentItem) {
    if (!selectedChannelId) return
    if (!window.confirm(`Удалить «${t.name}»?`)) return
    await run(`del:${t.id}`, () => api.deleteTalent(selectedChannelId, t.id), 'Удалено')
  }

  async function doImport() {
    if (!selectedChannelId) return
    await run('import', async () => {
      const r = await api.importTalents(selectedChannelId, importText)
      toast.success(`Импорт: новых ${r.created}, обновлено ${r.updated}`)
      setShowImport(false)
      setImportText('')
    })
  }

  async function syncSources() {
    if (!selectedChannelId) return
    await run('sync', async () => {
      const r = await api.syncTalentSources(selectedChannelId)
      toast.success(`Источники: создано ${r.created}, выключено ${r.disabled}`)
    })
  }

  const visible = items.filter((t) => {
    const q = filter.trim().toLowerCase()
    if (!q) return true
    return [t.name, t.agency, t.group, t.aliasesCsv].some((v) => (v ?? '').toLowerCase().includes(q))
  })

  if (!selectedChannelId) {
    return (<><PageHead title="Таланты" /><Card><Empty>Выберите канал.</Empty></Card></>)
  }

  return (
    <>
      <PageHead
        title="Таланты"
        subtitle="Кого освещает канал: имена, алиасы, агентства, официальные аккаунты. Из отмеченных аккаунтов создаются источники"
        actions={canEdit ? (
          <div className="row" style={{ gap: 8 }}>
            <Button variant="ghost" onClick={() => setShowImport((v) => !v)}><Upload size={15} /> Импорт</Button>
            <Button variant="ghost" loading={busy === 'sync'} onClick={syncSources}><RefreshCw size={15} /> Создать источники</Button>
            <Button variant="primary" onClick={startCreate}><Plus size={15} /> Добавить</Button>
          </div>
        ) : undefined}
      />

      {canEdit && showImport && (
        <Card title="Импорт списком" subtitle="Одна строка — один талант: Имя; Агентство; Ген; алиас1, алиас2; YouTube; X" actions={<Button variant="ghost" size="sm" onClick={() => setShowImport(false)}><X size={15} /></Button>}>
          <Textarea rows={8} value={importText} onChange={(e) => setImportText(e.target.value)} placeholder={'Gawr Gura; hololive; EN Myth; Gura, Same-chan, がうる・ぐら; https://www.youtube.com/@GawrGura; @gawrgura'} />
          <div className="row" style={{ marginTop: 8 }}>
            <Button variant="primary" loading={busy === 'import'} disabled={!importText.trim()} onClick={doImport}><Upload size={15} /> Импортировать</Button>
          </div>
        </Card>
      )}

      <Card title={`Реестр (${items.length})`} actions={<TextInput placeholder="Поиск…" value={filter} onChange={(e) => setFilter(e.target.value)} />}>
        {visible.length === 0 ? (
          <Empty>Пусто. Добавь талантов вручную или импортом.</Empty>
        ) : (
          <div className="table-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th>Имя</th><th>Агентство</th><th>Ген</th><th>Алиасы</th><th>Тир</th><th>YouTube</th><th>X</th><th>Статус</th>
                </tr>
              </thead>
              <tbody>
                {visible.map((t) => (
                  <tr key={t.id} onClick={() => startEdit(t)} style={canEdit ? { cursor: 'pointer' } : undefined}>
                    <td>{t.name}</td>
                    <td>{t.agency ?? '—'}</td>
                    <td>{t.group ?? '—'}</td>
                    <td className="faint" style={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{t.aliasesCsv ?? '—'}</td>
                    <td><Badge tone={t.priority === 1 ? 'green' : ''}>{PRIORITY_LABELS[t.priority] ?? t.priority}</Badge></td>
                    <td>{t.youTube ? <Badge tone={t.trackYouTube ? 'green' : ''}>{t.trackYouTube ? 'следим' : 'есть'}</Badge> : '—'}</td>
                    <td>{t.twitter ? <Badge tone={t.trackTwitter ? 'green' : ''}>{t.trackTwitter ? 'следим' : 'есть'}</Badge> : '—'}</td>
                    <td>{t.isActive ? <Badge tone="green">активен</Badge> : <Badge>выкл</Badge>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {canEdit && showForm && (
        <Card title={editingId ? 'Редактирование' : 'Новый талант'} actions={<Button variant="ghost" size="sm" onClick={() => setShowForm(false)}><X size={15} /> Закрыть</Button>}>
          <div className="grid cols-2">
            <Field label="Имя (латиницей, как в оригинале)"><TextInput value={form.name} onChange={(e) => patch('name', e.target.value)} /></Field>
            <Field label="Агентство"><TextInput value={form.agency ?? ''} onChange={(e) => patch('agency', e.target.value)} placeholder="hololive / NIJISANJI / VShojo / indie" /></Field>
            <Field label="Ген / группа"><TextInput value={form.group ?? ''} onChange={(e) => patch('group', e.target.value)} /></Field>
            <Field label="Тир">
              <Select value={form.priority} onChange={(e) => patch('priority', Number(e.target.value))}>
                <option value={1}>1 — топ, всегда инфоповод</option>
                <option value={2}>2 — обычный</option>
                <option value={3}>3 — нишевый</option>
              </Select>
            </Field>
            <Field label="Алиасы через запятую" hint="Ники, JP/RU написания: Gura, Same-chan, がうる・ぐら, Гура"><TextInput value={form.aliasesCsv ?? ''} onChange={(e) => patch('aliasesCsv', e.target.value)} /></Field>
            <Field label="YouTube" hint="Ссылка, @handle или UC-id"><TextInput value={form.youTube ?? ''} onChange={(e) => patch('youTube', e.target.value)} /></Field>
            <Field label="X (Twitter)" hint="@handle"><TextInput value={form.twitter ?? ''} onChange={(e) => patch('twitter', e.target.value)} /></Field>
            <Field label="Telegram" hint="Если есть свой канал"><TextInput value={form.telegram ?? ''} onChange={(e) => patch('telegram', e.target.value)} /></Field>
          </div>
          <Field label="Заметки"><Textarea value={form.notes ?? ''} onChange={(e) => patch('notes', e.target.value)} /></Field>
          <div className="row" style={{ gap: 16, flexWrap: 'wrap' }}>
            <Switch checked={form.trackYouTube} onChange={(v) => patch('trackYouTube', v)} label="Следить за YouTube (создать источник)" />
            <Switch checked={form.trackTwitter} onChange={(v) => patch('trackTwitter', v)} label="Следить за X (создать источник)" />
            <Switch checked={form.isActive} onChange={(v) => patch('isActive', v)} label="Активен" />
          </div>
          <div className="row" style={{ marginTop: 10, gap: 8 }}>
            <Button variant="primary" loading={busy === 'save'} disabled={!form.name.trim()} onClick={save}><Save size={15} /> Сохранить</Button>
            {editingId && (
              <Button variant="danger" loading={busy === `del:${editingId}`} onClick={() => { const t = items.find((i) => i.id === editingId); if (t) remove(t) }}>
                <Trash2 size={15} /> Удалить
              </Button>
            )}
          </div>
        </Card>
      )}
    </>
  )
}
