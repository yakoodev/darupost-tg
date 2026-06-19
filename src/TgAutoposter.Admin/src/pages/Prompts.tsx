import { useEffect, useState } from 'react'
import { Save } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { kindLabels } from '../lib/format'
import { Button, Card, Empty, Field, PageHead, Textarea } from '../components/ui'
import type { ChannelDetails, PublicationTypeItem } from '../lib/types'

export default function Prompts() {
  const { selectedChannelId, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [channel, setChannel] = useState<ChannelDetails | null>(null)
  const [types, setTypes] = useState<PublicationTypeItem[]>([])
  const [savingChannel, setSavingChannel] = useState(false)
  const [savingType, setSavingType] = useState<string>('')

  useEffect(() => {
    if (!selectedChannelId) {
      setChannel(null)
      setTypes([])
      return
    }
    let active = true
    api.channel(selectedChannelId)
      .then((c) => { if (active) setChannel(c) })
      .catch((e) => toast.error(e instanceof Error ? e.message : 'Ошибка загрузки канала'))
    api.publicationTypes(selectedChannelId)
      .then((list) => { if (active) setTypes(list) })
      .catch((e) => toast.error(e instanceof Error ? e.message : 'Ошибка загрузки типов'))
    return () => { active = false }
  }, [selectedChannelId, toast])

  async function saveChannel() {
    if (!channel) return
    setSavingChannel(true)
    try {
      await api.saveChannel(channel.id, {
        name: channel.name,
        telegramUsername: channel.telegramUsername ?? null,
        telegramChatId: channel.telegramChatId ?? null,
        timeZone: channel.timeZone,
        language: channel.language,
        positioning: channel.positioning,
        systemPrompt: channel.systemPrompt,
        styleGuide: channel.styleGuide,
        defaultModerationMode: channel.defaultModerationMode,
        dailyPostLimit: channel.dailyPostLimit,
        dailyAiBudgetLimit: channel.dailyAiBudgetLimit ?? null,
        isEnabled: channel.isEnabled,
      })
      toast.success('Сохранено')
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSavingChannel(false)
    }
  }

  function patchChannel(changes: Partial<ChannelDetails>) {
    setChannel((prev) => (prev ? { ...prev, ...changes } : prev))
  }

  function patchType(id: string, systemPrompt: string) {
    setTypes((prev) => prev.map((t) => (t.id === id ? { ...t, systemPrompt } : t)))
  }

  async function saveType(type: PublicationTypeItem) {
    setSavingType(type.id)
    try {
      await api.savePublicationType(selectedChannelId, type.id, {
        isEnabled: type.isEnabled,
        priority: type.priority,
        moderationMode: type.moderationMode,
        factCheckMode: type.factCheckMode,
        rumorPolicy: type.rumorPolicy,
        maxTextLength: type.maxTextLength,
        mediaMode: type.mediaMode,
        systemPrompt: type.systemPrompt,
        headerTemplate: type.headerTemplate ?? null,
        footerTemplate: type.footerTemplate ?? null,
      })
      toast.success('Сохранено')
      refresh()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setSavingType('')
    }
  }

  if (!selectedChannelId) {
    return (
      <>
        <PageHead title="Промпты" subtitle="Промпты канала и типов публикаций" />
        <Card><Empty>Выберите канал.</Empty></Card>
      </>
    )
  }

  return (
    <>
      <PageHead title="Промпты" subtitle="Промпты канала и типов публикаций" />
      {!canEdit && <Card><Empty>Режим только для просмотра — нужны права администратора канала.</Empty></Card>}

      <Card title="Промпт и стиль канала" subtitle={channel?.name}>
        {!channel
          ? <Empty>Загрузка…</Empty>
          : (
            <>
              <Field label="Позиционирование">
                <Textarea
                  value={channel.positioning}
                  disabled={!canEdit}
                  style={{ minHeight: 100 }}
                  onChange={(e) => patchChannel({ positioning: e.target.value })}
                />
              </Field>
              <Field label="Системный промпт">
                <Textarea
                  value={channel.systemPrompt}
                  disabled={!canEdit}
                  style={{ minHeight: 160 }}
                  onChange={(e) => patchChannel({ systemPrompt: e.target.value })}
                />
              </Field>
              <Field label="Гайд по стилю">
                <Textarea
                  value={channel.styleGuide}
                  disabled={!canEdit}
                  style={{ minHeight: 120 }}
                  onChange={(e) => patchChannel({ styleGuide: e.target.value })}
                />
              </Field>
              <div className="row" style={{ marginTop: 8 }}>
                <Button variant="primary" size="sm" disabled={!canEdit} loading={savingChannel} onClick={saveChannel}>
                  <Save size={15} /> Сохранить
                </Button>
              </div>
            </>
          )}
      </Card>

      <Card title="Промпты типов публикаций">
        {types.length === 0
          ? <Empty>Нет типов публикаций.</Empty>
          : (
            <div className="grid" style={{ gap: 14 }}>
              {types.map((t) => (
                <Field key={t.id} label={kindLabels[t.kind] ?? t.name}>
                  <Textarea
                    value={t.systemPrompt}
                    disabled={!canEdit}
                    style={{ minHeight: 120 }}
                    onChange={(e) => patchType(t.id, e.target.value)}
                  />
                  <div className="row" style={{ marginTop: 8 }}>
                    <Button variant="primary" size="sm" disabled={!canEdit} loading={savingType === t.id} onClick={() => saveType(t)}>
                      <Save size={15} /> Сохранить
                    </Button>
                  </div>
                </Field>
              ))}
            </div>
          )}
      </Card>
    </>
  )
}
