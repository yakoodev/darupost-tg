import { useEffect, useState } from 'react'
import { Save } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { kindLabels } from '../lib/format'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, Textarea, TextInput } from '../components/ui'
import type { FactCheckMode, MediaGenerationMode, ModerationMode, PublicationTypeItem, RumorPolicy } from '../lib/types'

export default function PublicationTypes() {
  const { selectedChannelId, refresh } = useAppData()
  const { canOn } = useAuth()
  const toast = useToast()
  const canEdit = canOn(selectedChannelId, 'ChannelAdmin')

  const [types, setTypes] = useState<PublicationTypeItem[]>([])
  const [saving, setSaving] = useState<string>('')

  useEffect(() => {
    if (!selectedChannelId) {
      setTypes([])
      return
    }
    let active = true
    api.publicationTypes(selectedChannelId)
      .then((list) => { if (active) setTypes(list) })
      .catch((e) => toast.error(e instanceof Error ? e.message : 'Ошибка загрузки'))
    return () => { active = false }
  }, [selectedChannelId, toast])

  function patch(id: string, changes: Partial<PublicationTypeItem>) {
    setTypes((prev) => prev.map((t) => (t.id === id ? { ...t, ...changes } : t)))
  }

  async function save(type: PublicationTypeItem) {
    setSaving(type.id)
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
      setSaving('')
    }
  }

  return (
    <>
      <PageHead title="Типы публикаций" subtitle="Настройки модерации, фактчека и медиа по каждому типу" />
      {!canEdit && <Card><Empty>Режим только для просмотра — нужны права администратора канала.</Empty></Card>}
      {(!selectedChannelId || types.length === 0)
        ? <Card><Empty>Нет типов публикаций для этого канала.</Empty></Card>
        : (
          <div className="grid cols-2">
            {types.map((t) => (
              <Card
                key={t.id}
                title={kindLabels[t.kind] ?? t.name}
                subtitle={t.description}
                actions={<Badge tone={t.isEnabled ? 'green' : ''}>{t.isEnabled ? 'вкл' : 'выкл'}</Badge>}
              >
                <Field>
                  <Switch checked={t.isEnabled} onChange={(v) => patch(t.id, { isEnabled: v })} label="Включён" />
                </Field>

                <Field label="Приоритет">
                  <TextInput
                    type="number"
                    value={t.priority}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { priority: Number(e.target.value) })}
                  />
                </Field>

                <Field label="Модерация">
                  <Select
                    value={t.moderationMode}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { moderationMode: e.target.value as ModerationMode })}
                  >
                    <option value="Manual">Ручная</option>
                    <option value="Automatic">Автоматическая</option>
                  </Select>
                </Field>

                <Field label="Фактчек">
                  <Select
                    value={t.factCheckMode}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { factCheckMode: e.target.value as FactCheckMode })}
                  >
                    <option value="Soft">Soft</option>
                    <option value="Medium">Medium</option>
                    <option value="Strict">Strict</option>
                    <option value="Custom">Custom</option>
                  </Select>
                </Field>

                <Field label="Политика слухов">
                  <Select
                    value={t.rumorPolicy}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { rumorPolicy: e.target.value as RumorPolicy })}
                  >
                    <option value="Deny">Deny</option>
                    <option value="AllowWithLabel">AllowWithLabel</option>
                    <option value="WhitelistedOnly">WhitelistedOnly</option>
                    <option value="AlwaysManual">AlwaysManual</option>
                  </Select>
                </Field>

                <Field label="Медиа">
                  <Select
                    value={t.mediaMode}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { mediaMode: e.target.value as MediaGenerationMode })}
                  >
                    <option value="None">None</option>
                    <option value="UseSourceImage">UseSourceImage</option>
                    <option value="GeneratePoster">GeneratePoster</option>
                    <option value="TranslateMeme">TranslateMeme</option>
                  </Select>
                </Field>

                <Field label="Макс. длина текста">
                  <TextInput
                    type="number"
                    value={t.maxTextLength}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { maxTextLength: Number(e.target.value) })}
                  />
                </Field>

                <Field label="Системный промпт">
                  <Textarea
                    value={t.systemPrompt}
                    disabled={!canEdit}
                    style={{ minHeight: 120 }}
                    onChange={(e) => patch(t.id, { systemPrompt: e.target.value })}
                  />
                </Field>

                <Field label="Шапка">
                  <TextInput
                    value={t.headerTemplate ?? ''}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { headerTemplate: e.target.value })}
                  />
                </Field>

                <Field label="Подвал">
                  <TextInput
                    value={t.footerTemplate ?? ''}
                    disabled={!canEdit}
                    onChange={(e) => patch(t.id, { footerTemplate: e.target.value })}
                  />
                </Field>

                <div className="row" style={{ marginTop: 8 }}>
                  <Button variant="primary" size="sm" disabled={!canEdit} loading={saving === t.id} onClick={() => save(t)}>
                    <Save size={15} /> Сохранить
                  </Button>
                </div>
              </Card>
            ))}
          </div>
        )}
    </>
  )
}
