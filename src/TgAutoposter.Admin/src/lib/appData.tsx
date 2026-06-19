import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { useRealtime, type LiveState } from './realtime'
import type { AiAccountStatus, ChannelListItem, Dashboard, WorkerStatus } from './types'

interface AppDataValue {
  channels: ChannelListItem[]
  selectedChannelId: string
  selectChannel: (id: string) => void
  selectedChannel?: ChannelListItem
  dashboard: Dashboard | null
  polza: AiAccountStatus | null
  worker: WorkerStatus | null
  live: LiveState
  refresh: () => void
}

const AppDataContext = createContext<AppDataValue | null>(null)
const CHANNEL_KEY = 'tg.selectedChannel'

export function AppDataProvider({ children }: { children: ReactNode }) {
  const [channels, setChannels] = useState<ChannelListItem[]>([])
  const [selectedChannelId, setSelectedChannelId] = useState<string>(() => localStorage.getItem(CHANNEL_KEY) ?? '')
  const [dashboard, setDashboard] = useState<Dashboard | null>(null)
  const [polza, setPolza] = useState<AiAccountStatus | null>(null)
  const [worker, setWorker] = useState<WorkerStatus | null>(null)

  const selectChannel = useCallback((id: string) => {
    setSelectedChannelId(id)
    localStorage.setItem(CHANNEL_KEY, id)
  }, [])

  const refresh = useCallback(async () => {
    try {
      const list = await api.channels()
      setChannels(list)

      let current = selectedChannelId
      if (!current || !list.some((c) => c.id === current)) {
        current = list[0]?.id ?? ''
        setSelectedChannelId(current)
        if (current) localStorage.setItem(CHANNEL_KEY, current)
      }

      const [dash, polzaStatus, workerStatus] = await Promise.allSettled([
        api.dashboard(current || undefined),
        api.polzaStatus(),
        api.workerStatus(),
      ])
      if (dash.status === 'fulfilled') setDashboard(dash.value)
      if (polzaStatus.status === 'fulfilled') setPolza(polzaStatus.value)
      if (workerStatus.status === 'fulfilled') setWorker(workerStatus.value)
    } catch {
      /* surfaced elsewhere; keep last good state */
    }
  }, [selectedChannelId])

  useEffect(() => { refresh() }, [refresh])
  const live = useRealtime(refresh)

  const value: AppDataValue = {
    channels,
    selectedChannelId,
    selectChannel,
    selectedChannel: channels.find((c) => c.id === selectedChannelId),
    dashboard,
    polza,
    worker,
    live,
    refresh,
  }

  return <AppDataContext.Provider value={value}>{children}</AppDataContext.Provider>
}

export function useAppData() {
  const ctx = useContext(AppDataContext)
  if (!ctx) throw new Error('useAppData must be used within AppDataProvider')
  return ctx
}
