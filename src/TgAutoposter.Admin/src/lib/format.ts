import type { PostStatus } from './types'

export function money(value?: number | null, currency = '₽') {
  if (value == null) return '—'
  return `${currency}${Number(value).toFixed(2)}`
}

export function dateTime(value?: string | null) {
  if (!value) return '—'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
}

export const statusLabels: Record<PostStatus, string> = {
  CandidateFound: 'найден',
  WaitingFactCheck: 'фактчек',
  FactCheckFailed: 'фактчек провален',
  Duplicate: 'дубль',
  GeneratingText: 'генерация',
  GeneratingImage: 'картинка',
  WaitingModeration: 'модерация',
  NeedsRewrite: 'переделать',
  Scheduled: 'запланирован',
  Published: 'опубликован',
  PublishFailed: 'ошибка',
  Rejected: 'отклонён',
}

export function statusTone(status: PostStatus): 'green' | 'amber' | 'red' | 'blue' | '' {
  switch (status) {
    case 'Published': return 'green'
    case 'Scheduled': return 'blue'
    case 'WaitingModeration': case 'NeedsRewrite': case 'GeneratingText': case 'GeneratingImage': return 'amber'
    case 'Rejected': case 'PublishFailed': case 'FactCheckFailed': case 'Duplicate': return 'red'
    default: return ''
  }
}

export const kindLabels: Record<string, string> = {
  News: 'Новость', BreakingNews: 'Срочно', Rumor: 'Слух', Meme: 'Мем',
  Digest: 'Дайджест', Deal: 'Раздача', Trailer: 'Трейлер',
}

export const roleLabels: Record<string, string> = {
  Owner: 'Владелец', ChannelAdmin: 'Админ канала', Moderator: 'Модератор',
}

export const weekdays = ['Вс', 'Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб']
