import { useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import {
  Bot, LayoutDashboard, Inbox, Radio, Rss, ListChecks, CalendarClock,
  MessageSquareText, Users, History, Wallet, Plug, LogOut, Menu, Power,
} from 'lucide-react'
import { useAppData } from '../lib/appData'
import { useAuth } from '../lib/auth'

const NAV = [
  { to: '/', label: 'Обзор', icon: LayoutDashboard, end: true },
  { to: '/queue', label: 'Очередь', icon: Inbox, badge: 'queue' },
  { to: '/channels', label: 'Каналы', icon: Radio },
  { to: '/sources', label: 'Источники', icon: Rss },
  { to: '/types', label: 'Типы публикаций', icon: ListChecks },
  { to: '/prompts', label: 'Промпты', icon: MessageSquareText },
  { to: '/schedule', label: 'Расписание', icon: CalendarClock },
  { to: '/history', label: 'История', icon: History },
  { to: '/costs', label: 'Расходы', icon: Wallet },
  { to: '/users', label: 'Пользователи', icon: Users, ownerOnly: true },
  { to: '/integrations', label: 'Интеграции', icon: Plug },
] as const

export default function Layout() {
  const { channels, selectedChannelId, selectChannel, selectedChannel, dashboard, live } = useAppData()
  const { user, logout } = useAuth()
  const [open, setOpen] = useState(false)

  const liveTone = live === 'live' ? 'green' : live === 'connecting' ? 'amber' : 'red'

  return (
    <div className="shell">
      {open && <div className="sidebar-backdrop" onClick={() => setOpen(false)} />}
      <aside className={`sidebar ${open ? 'open' : ''}`}>
        <div className="brand">
          <div className="brand-logo"><Bot size={19} /></div>
          <div>
            <div className="brand-title">TG Autoposter</div>
            <div className="brand-sub">SMM control</div>
          </div>
        </div>

        <nav>
          {NAV.map((item) => {
            if ('ownerOnly' in item && item.ownerOnly && !user?.isGlobalOwner) return null
            const Icon = item.icon
            const badge = 'badge' in item && item.badge === 'queue' && dashboard?.waitingModeration
              ? dashboard.waitingModeration : null
            return (
              <NavLink
                key={item.to}
                to={item.to}
                end={'end' in item ? item.end : false}
                className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`}
                onClick={() => setOpen(false)}
              >
                <Icon size={17} />
                <span>{item.label}</span>
                {badge ? <span className="nav-badge">{badge}</span> : null}
              </NavLink>
            )
          })}
        </nav>

        <div style={{ flex: 1 }} />
        <div className="nav-item" style={{ cursor: 'default' }}>
          <span className={`dot`} style={{ color: `var(--${liveTone})` }} />
          <span className="faint" style={{ fontSize: 12 }}>{live === 'live' ? 'онлайн' : live === 'connecting' ? 'подключение' : 'офлайн'}</span>
        </div>
        <button className="nav-item" onClick={logout}>
          <LogOut size={17} />
          <span>{user?.displayName ?? 'Выйти'}</span>
        </button>
      </aside>

      <div className="main">
        <header className="topbar">
          <button className="btn ghost sm menu-btn" onClick={() => setOpen(true)}><Menu size={18} /></button>
          <div className="chan-switch">
            <select value={selectedChannelId} onChange={(e) => selectChannel(e.target.value)}>
              {channels.length === 0 && <option value="">Нет каналов</option>}
              {channels.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </div>
          {selectedChannel && (() => {
            const mode = !selectedChannel.isEnabled
              ? { tone: 'red', label: 'выключен' }
              : selectedChannel.defaultModerationMode === 'Automatic'
                ? { tone: 'green', label: 'авторежим' }
                : { tone: 'amber', label: 'с модерацией' }
            return (
              <span className={`badge ${mode.tone}`}>
                <Power size={12} />
                {mode.label}
              </span>
            )
          })()}
          <div className="spacer" />
          {selectedChannel?.telegramUsername && (
            <span className="faint mono">{selectedChannel.telegramUsername}</span>
          )}
        </header>
        <div className="content">
          <Outlet />
        </div>
      </div>
    </div>
  )
}
