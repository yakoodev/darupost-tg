import { useAppData } from '../lib/appData'
import { Badge, Card, PageHead, Stat } from '../components/ui'
import { money } from '../lib/format'

export default function Integrations() {
  const { polza, worker, selectedChannel } = useAppData()

  return (
    <>
      <PageHead title="Интеграции" subtitle="Подключения настраиваются через .env / переменные окружения" />

      <div className="grid cols-2">
        <Card
          title="Polza.ai"
          subtitle="AI-провайдер"
          actions={<Badge tone={polza?.enabled ? 'green' : 'amber'}>{polza?.enabled ? 'включён' : 'fallback'}</Badge>}
        >
          <div className="grid cols-2">
            <Stat label="Модель текста" value={polza?.defaultModel ?? '—'} />
            <Stat label="Модель картинок" value={polza?.imageModel ?? '—'} />
          </div>
          <div className="divider" />
          <div className="faint mono" style={{ fontSize: 12, wordBreak: 'break-all' }}>{polza?.baseUrl ?? '—'}</div>
          <div className="row" style={{ marginTop: 12, alignItems: 'center' }}>
            <span className="muted">API-ключ</span>
            <Badge tone={polza?.hasApiKey ? 'green' : 'red'}>{polza?.hasApiKey ? 'есть' : 'нет'}</Badge>
          </div>
          {polza?.balanceRub != null && (
            <div style={{ marginTop: 8 }}>Баланс: <b>{money(polza.balanceRub)}</b></div>
          )}
          {polza?.error && <div style={{ marginTop: 8 }}><Badge tone="red">{polza.error}</Badge></div>}
        </Card>

        <Card title="Telegram-бот" subtitle="Публикация в каналы">
          <div className="faint" style={{ fontSize: 12.5 }}>
            Токен бота и чат модерации задаются через переменные окружения
            <span className="mono"> TELEGRAM_BOT_TOKEN</span> и
            <span className="mono"> TELEGRAM_MODERATION_CHAT_ID</span>.
          </div>
          <div className="divider" />
          <div className="row" style={{ alignItems: 'center' }}>
            <span className="muted">Канал</span>
            <span className="mono">{selectedChannel?.telegramUsername ?? '—'}</span>
          </div>
        </Card>
      </div>

      <Card title="Worker (автопостинг)" subtitle="Фоновый запуск пайплайна"
        actions={<Badge tone={worker?.enabled ? 'green' : 'amber'}>{worker?.enabled ? 'включён' : 'выключен'}</Badge>}
      >
        <div className="grid cols-2">
          <Stat label="Интервал, мин" value={worker?.intervalMinutes ?? '—'} />
          <Stat label="Постов за прогон" value={worker?.maxPostsPerRun ?? '—'} />
        </div>
      </Card>
    </>
  )
}
