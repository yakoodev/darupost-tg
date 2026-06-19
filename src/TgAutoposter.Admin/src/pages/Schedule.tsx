import { useCallback, useEffect, useState } from 'react'
import { CalendarClock, Plus, Save, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { weekdays } from '../lib/format'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, TextInput } from '../components/ui'
import type { ScheduleWindowItem } from '../lib/types'

function defaultWindow(): ScheduleWindowItem {
  return {
    dayOfWeek: null,
    startTime: '10:00',
    endTime: '12:00',
    minimumIntervalMinutes: 60,
    allowBreakingNewsBypass: false,
  }
}

export default function Schedule() {
  const { selectedChannelId, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [windows, setWindows] = useState<ScheduleWindowItem[]>([])
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    if (!selectedChannelId) {
      setWindows([])
      return
    }
    const items = await api.schedule(selectedChannelId)
    setWindows(items)
  }, [selectedChannelId])

  useEffect(() => {
    void load()
  }, [load])

  const updateWindow = (index: number, patch: Partial<ScheduleWindowItem>) => {
    setWindows((cur) => cur.map((w, i) => (i === index ? { ...w, ...patch } : w)))
  }

  const removeWindow = (index: number) => {
    setWindows((cur) => cur.filter((_, i) => i !== index))
  }

  const addWindow = () => {
    setWindows((cur) => [...cur, defaultWindow()])
  }

  const save = async () => {
    if (!selectedChannelId) return
    setSaving(true)
    try {
      await api.saveSchedule(selectedChannelId, windows)
      toast.success('Расписание сохранено')
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSaving(false)
    }
  }

  if (!selectedChannelId) {
    return (
      <>
        <PageHead title="Расписание" subtitle="Окна публикаций" />
        <Empty>Выберите канал, чтобы настроить расписание.</Empty>
      </>
    )
  }

  return (
    <>
      <PageHead
        title="Расписание"
        subtitle="Окна публикаций"
        actions={
          canEdit && (
            <Button variant="primary" onClick={() => void save()} loading={saving}>
              <Save size={16} /> Сохранить расписание
            </Button>
          )
        }
      />

      {!canEdit && <Badge tone="warning">Только просмотр</Badge>}

      <Card
        title="Окна публикаций"
        subtitle="Посты публикуются только внутри окон, ночью — нет."
        actions={
          canEdit && (
            <Button size="sm" onClick={addWindow}>
              <Plus size={16} /> Добавить окно
            </Button>
          )
        }
      >
        {windows.length === 0 ? (
          <Empty>
            <CalendarClock size={20} /> Окна не заданы.
          </Empty>
        ) : (
          windows.map((w, i) => (
            <div key={w.id ?? `new-${i}`} className="card">
              <Field label="День недели">
                <Select
                  value={w.dayOfWeek == null ? '' : String(w.dayOfWeek)}
                  disabled={!canEdit}
                  onChange={(e) => {
                    const v = e.target.value
                    updateWindow(i, { dayOfWeek: v === '' ? null : Number(v) })
                  }}
                >
                  <option value="">Любой день</option>
                  {weekdays.map((label, day) => (
                    <option key={day} value={String(day)}>
                      {label}
                    </option>
                  ))}
                </Select>
              </Field>

              <Field label="Начало">
                <TextInput
                  type="time"
                  value={w.startTime}
                  disabled={!canEdit}
                  onChange={(e) => updateWindow(i, { startTime: e.target.value })}
                />
              </Field>

              <Field label="Конец">
                <TextInput
                  type="time"
                  value={w.endTime}
                  disabled={!canEdit}
                  onChange={(e) => updateWindow(i, { endTime: e.target.value })}
                />
              </Field>

              <Field label="Мин. интервал (мин)">
                <TextInput
                  type="number"
                  value={String(w.minimumIntervalMinutes)}
                  disabled={!canEdit}
                  onChange={(e) => updateWindow(i, { minimumIntervalMinutes: Number(e.target.value) })}
                />
              </Field>

              <Switch
                checked={w.allowBreakingNewsBypass}
                label="срочные в обход расписания"
                onChange={(v) => canEdit && updateWindow(i, { allowBreakingNewsBypass: v })}
              />

              {canEdit && (
                <Button variant="danger" size="sm" onClick={() => removeWindow(i)}>
                  <Trash2 size={16} /> Удалить
                </Button>
              )}
            </div>
          ))
        )}
      </Card>
    </>
  )
}
