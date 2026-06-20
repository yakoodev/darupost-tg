import { useState } from 'react'
import { Eye, Power, Sparkles, Zap } from 'lucide-react'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { api } from '../lib/api'
import { money } from '../lib/format'
import { kindLabels } from '../lib/format'
import { Button, Card, PageHead, Select, Stat } from '../components/ui'
import type { ChannelMode, PublicationKind } from '../lib/types'

const KINDS: PublicationKind[] = ['News', 'Rumor', 'Meme', 'Digest', 'Deal', 'Trailer']
const MODE_LABELS: Record<ChannelMode, string> = { Off: 'Выключен', Moderated: 'С модерацией', Auto: 'Авто' }

export default function Dashboard() {
  const { dashboard, selectedChannel, selectedChannelId, polza, worker, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const [kind, setKind] = useState<PublicationKind>('News')
  const [busy, setBusy] = useState('')

  const isAdmin = canOn(selectedChannelId, 'ChannelAdmin')
  const currentMode: ChannelMode = !selectedChannel?.isEnabled
    ? 'Off'
    : selectedChannel.defaultModerationMode === 'Automatic'
      ? 'Auto'
      : 'Moderated'

  async function run(action: 'generate' | 'now') {
    if (!selectedChannelId) return
    setBusy(action)
    try {
      if (action === 'generate') {
        const r = await api.runPipeline(selectedChannelId, { maxPostsToCreate: 1, publicationKind: kind, ignoreSourceSchedule: true })
        toast.success(`Готово: создано ${r.postsCreated}, дублей ${r.duplicatesSkipped}`)
      } else {
        const r = await api.runPipeline(selectedChannelId, { maxPostsToCreate: 1, publishNewPostsImmediately: true, ignoreSourceSchedule: true, bypassDailyLimit: true })
        toast.success(`Запуск: опубликовано ${r.publishedThisRun}, создано ${r.postsCreated}`)
      }
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  async function changeMode(mode: ChannelMode) {
    if (!selectedChannelId || mode === currentMode) return
    setBusy(`mode:${mode}`)
    try {
      await api.setMode(selectedChannelId, mode)
      toast.success(`Режим: ${MODE_LABELS[mode]}`)
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  const d = dashboard

  return (
    <>
      <PageHead
        title={selectedChannel?.name ?? 'Обзор'}
        subtitle="Сводка по каналу и быстрый запуск пайплайна"
        actions={isAdmin && (
          <div className="row" style={{ gap: 6 }}>
            <Button size="sm" variant={currentMode === 'Off' ? 'danger' : 'ghost'} loading={busy === 'mode:Off'} onClick={() => changeMode('Off')}>
              <Power size={15} /> Выключен
            </Button>
            <Button size="sm" variant={currentMode === 'Moderated' ? 'primary' : 'ghost'} loading={busy === 'mode:Moderated'} onClick={() => changeMode('Moderated')}>
              <Eye size={15} /> С модерацией
            </Button>
            <Button size="sm" variant={currentMode === 'Auto' ? 'success' : 'ghost'} loading={busy === 'mode:Auto'} onClick={() => changeMode('Auto')}>
              <Zap size={15} /> Авто
            </Button>
          </div>
        )}
      />

      {isAdmin && (
        <Card className="pad-lg" title="Запуск" subtitle="Собрать инфоповод и подготовить пост"
          actions={<div className="row" style={{ alignItems: 'center' }}>
            <Select value={kind} onChange={(e) => setKind(e.target.value as PublicationKind)} style={{ width: 160 }}>
              {KINDS.map((k) => <option key={k} value={k}>{kindLabels[k]}</option>)}
            </Select>
            <Button variant="primary" loading={busy === 'generate'} onClick={() => run('generate')}><Sparkles size={16} /> Сгенерировать</Button>
            <Button loading={busy === 'now'} onClick={() => run('now')}><Zap size={16} /> Запустить сейчас</Button>
          </div>}
        >
          <div className="faint" style={{ fontSize: 12.5 }}>
            «Сгенерировать» создаёт один черновик выбранного типа и отправляет на модерацию.
            «Запустить сейчас» дополнительно публикует его, минуя расписание и дневной лимит.
          </div>
        </Card>
      )}

      <div className="grid cols-4" style={{ marginTop: 16 }}>
        <Stat label="Очередь сегодня" value={d?.queueToday ?? '—'} />
        <Stat label="На модерации" value={d?.waitingModeration ?? '—'} accent={!!d?.waitingModeration} />
        <Stat label="Опубликовано сегодня" value={d?.publishedToday ?? '—'} />
        <Stat label="Опубликовано за месяц" value={d?.publishedMonth ?? '—'} />
        <Stat label="Отклонено" value={d?.rejected ?? '—'} />
        <Stat label="Дублей найдено" value={d?.duplicatesFound ?? '—'} />
        <Stat label="Ошибки публикации" value={d?.publishErrors ?? '—'} />
        <Stat label="Источников активно" value={d?.enabledSources ?? '—'} />
        <Stat label="Расход сегодня" value={money(d?.aiSpendToday)} />
        <Stat label="Расход за месяц" value={money(d?.aiSpendMonth)} accent />
        <Stat label="Средняя цена поста" value={money(d?.averagePublishedPostCost)} />
        <Stat label="Polza факт сегодня" value={money(d?.providerSpendToday)} />
      </div>

      <div className="grid cols-2" style={{ marginTop: 16 }}>
        <Card title="AI-провайдер">
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <span className="muted">Polza.ai</span>
            <span className={`badge ${polza?.enabled ? 'green' : 'amber'}`}>{polza?.enabled ? 'включён' : 'fallback'}</span>
          </div>
          <div className="divider" />
          <div className="faint mono" style={{ fontSize: 12 }}>{polza?.defaultModel ?? '—'}</div>
          {polza?.balanceRub != null && <div style={{ marginTop: 8 }}>Баланс: <b>{money(polza.balanceRub)}</b></div>}
          {polza?.error && <div className="badge red" style={{ marginTop: 8 }}>{polza.error}</div>}
        </Card>
        <Card title="Автопостинг (worker)">
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <span className="muted">Фоновый запуск</span>
            <span className={`badge ${worker?.enabled ? 'green' : 'amber'}`}>{worker?.enabled ? 'включён' : 'выключен'}</span>
          </div>
          <div className="divider" />
          <div className="faint">Интервал: <b>{worker?.intervalMinutes ?? '—'} мин</b> · постов за прогон: <b>{worker?.maxPostsPerRun ?? '—'}</b></div>
        </Card>
      </div>
    </>
  )
}
