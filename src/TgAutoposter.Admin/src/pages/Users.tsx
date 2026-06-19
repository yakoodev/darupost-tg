import { useEffect, useState } from 'react'
import { Plus, Save, Shield, UserPlus } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { useToast } from '../lib/toast'
import { Badge, Button, Card, Empty, Field, PageHead, Select, Switch, TextInput } from '../components/ui'
import type { ChannelRoleType, UserListItem } from '../lib/types'
import { roleLabels } from '../lib/format'

interface CreateForm {
  displayName: string
  email: string
  password: string
  telegramUsername: string
  isGlobalOwner: boolean
}

const emptyCreate: CreateForm = { displayName: '', email: '', password: '', telegramUsername: '', isGlobalOwner: false }

interface EditState {
  isEnabled: boolean
  isGlobalOwner: boolean
  newPassword: string
}

interface RoleDraft {
  channelId: string
  role: ChannelRoleType
}

const roleOptions: ChannelRoleType[] = ['Owner', 'ChannelAdmin', 'Moderator']

export default function Users() {
  const { channels } = useAppData()
  const toast = useToast()

  const [users, setUsers] = useState<UserListItem[]>([])
  const [showCreate, setShowCreate] = useState(false)
  const [createForm, setCreateForm] = useState<CreateForm>(emptyCreate)
  const [busy, setBusy] = useState('')
  const [edits, setEdits] = useState<Record<string, EditState>>({})
  const [roleDrafts, setRoleDrafts] = useState<Record<string, RoleDraft>>({})

  async function reload() {
    try {
      const list = await api.users()
      setUsers(list)
      setEdits(Object.fromEntries(list.map((u) => [u.id, { isEnabled: u.isEnabled, isGlobalOwner: u.isGlobalOwner, newPassword: '' }])))
      setRoleDrafts((cur) =>
        Object.fromEntries(list.map((u) => [u.id, cur[u.id] ?? { channelId: channels[0]?.id ?? '', role: 'Moderator' }])),
      )
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    }
  }

  useEffect(() => {
    reload()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function create() {
    setBusy('create')
    try {
      await api.createUser({
        displayName: createForm.displayName,
        email: createForm.email || undefined,
        password: createForm.password || undefined,
        telegramUsername: createForm.telegramUsername || undefined,
        isGlobalOwner: createForm.isGlobalOwner,
      })
      toast.success('Пользователь создан')
      setShowCreate(false)
      setCreateForm(emptyCreate)
      await reload()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  function patchEdit(id: string, partial: Partial<EditState>) {
    setEdits((cur) => ({ ...cur, [id]: { ...cur[id], ...partial } }))
  }

  function patchRoleDraft(id: string, partial: Partial<RoleDraft>) {
    setRoleDrafts((cur) => ({ ...cur, [id]: { ...cur[id], ...partial } }))
  }

  async function saveUser(user: UserListItem) {
    const edit = edits[user.id]
    if (!edit) return
    setBusy(`save:${user.id}`)
    try {
      await api.updateUser(user.id, {
        displayName: user.displayName,
        email: user.email || undefined,
        telegramUsername: user.telegramUsername || undefined,
        isEnabled: edit.isEnabled,
        isGlobalOwner: edit.isGlobalOwner,
        newPassword: edit.newPassword || undefined,
      })
      toast.success('Сохранено')
      await reload()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  async function assignRole(user: UserListItem) {
    const draft = roleDrafts[user.id]
    if (!draft || !draft.channelId) return
    setBusy(`role:${user.id}`)
    try {
      await api.assignRole(user.id, draft.channelId, draft.role)
      toast.success('Роль выдана')
      await reload()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Ошибка')
    } finally {
      setBusy('')
    }
  }

  const actions = (
    <Button variant="primary" onClick={() => setShowCreate((v) => !v)}>
      <UserPlus size={16} /> Добавить пользователя
    </Button>
  )

  return (
    <>
      <PageHead title="Пользователи и роли" subtitle={`${users.length}`} actions={actions} />

      {showCreate && (
        <Card className="pad-lg" title="Новый пользователь">
          <div className="grid cols-2">
            <Field label="Имя">
              <TextInput value={createForm.displayName} onChange={(e) => setCreateForm({ ...createForm, displayName: e.target.value })} />
            </Field>
            <Field label="Email">
              <TextInput type="email" value={createForm.email} onChange={(e) => setCreateForm({ ...createForm, email: e.target.value })} />
            </Field>
            <Field label="Пароль">
              <TextInput type="password" value={createForm.password} onChange={(e) => setCreateForm({ ...createForm, password: e.target.value })} />
            </Field>
            <Field label="Telegram-username">
              <TextInput value={createForm.telegramUsername} onChange={(e) => setCreateForm({ ...createForm, telegramUsername: e.target.value })} />
            </Field>
          </div>
          <div className="divider" />
          <Switch checked={createForm.isGlobalOwner} onChange={(v) => setCreateForm({ ...createForm, isGlobalOwner: v })} label="Глобальный владелец" />
          <div className="row" style={{ marginTop: 12 }}>
            <Button variant="primary" loading={busy === 'create'} disabled={!createForm.displayName} onClick={create}>
              <Plus size={15} /> Создать
            </Button>
            <Button variant="ghost" onClick={() => setShowCreate(false)}>Отмена</Button>
          </div>
        </Card>
      )}

      {users.length === 0 && <Card><Empty>Пользователей пока нет.</Empty></Card>}

      {users.map((user) => {
        const edit = edits[user.id]
        const draft = roleDrafts[user.id]
        return (
          <Card
            key={user.id}
            className="pad-lg"
            title={user.displayName}
            actions={
              <div className="row">
                {user.isGlobalOwner && <Badge tone="accent">владелец</Badge>}
                <Badge tone={user.isEnabled ? 'green' : 'red'}>{user.isEnabled ? 'активен' : 'выключен'}</Badge>
              </div>
            }
          >
            <div className="grid cols-2">
              <Field label="Email">
                <TextInput className="mono" value={user.email ?? ''} disabled readOnly />
              </Field>
              <Field label="Telegram-username">
                <TextInput value={user.telegramUsername ?? ''} disabled readOnly />
              </Field>
            </div>

            <div className="divider" />

            <Field label="Роли">
              {user.roles.length === 0 ? (
                <span className="hint">Ролей нет.</span>
              ) : (
                <div className="row" style={{ flexWrap: 'wrap', gap: 6 }}>
                  {user.roles.map((r) => (
                    <Badge key={`${r.channelId}:${r.role}`}>{`${r.channelName}: ${roleLabels[r.role] ?? r.role}`}</Badge>
                  ))}
                </div>
              )}
            </Field>

            <div className="divider" />

            <Field label="Выдать роль">
              <div className="row" style={{ gap: 8, alignItems: 'flex-end' }}>
                <Select
                  value={draft?.channelId ?? ''}
                  onChange={(e) => patchRoleDraft(user.id, { channelId: e.target.value })}
                >
                  <option value="" disabled>Канал…</option>
                  {channels.map((c) => (
                    <option key={c.id} value={c.id}>{c.name}</option>
                  ))}
                </Select>
                <Select
                  value={draft?.role ?? 'Moderator'}
                  onChange={(e) => patchRoleDraft(user.id, { role: e.target.value as ChannelRoleType })}
                >
                  {roleOptions.map((r) => (
                    <option key={r} value={r}>{roleLabels[r] ?? r}</option>
                  ))}
                </Select>
                <Button
                  variant="primary"
                  size="sm"
                  loading={busy === `role:${user.id}`}
                  disabled={!draft?.channelId}
                  onClick={() => assignRole(user)}
                >
                  <Shield size={15} /> Выдать
                </Button>
              </div>
            </Field>

            <div className="divider" />

            <div className="row" style={{ gap: 16, flexWrap: 'wrap', alignItems: 'flex-end' }}>
              <Switch checked={edit?.isEnabled ?? user.isEnabled} onChange={(v) => patchEdit(user.id, { isEnabled: v })} label="Активен" />
              <Switch checked={edit?.isGlobalOwner ?? user.isGlobalOwner} onChange={(v) => patchEdit(user.id, { isGlobalOwner: v })} label="Глобальный владелец" />
              <Field label="Новый пароль">
                <TextInput
                  type="password"
                  placeholder="новый пароль"
                  value={edit?.newPassword ?? ''}
                  onChange={(e) => patchEdit(user.id, { newPassword: e.target.value })}
                />
              </Field>
              <Button variant="primary" size="sm" loading={busy === `save:${user.id}`} onClick={() => saveUser(user)}>
                <Save size={15} /> Сохранить
              </Button>
            </div>
          </Card>
        )
      })}
    </>
  )
}
