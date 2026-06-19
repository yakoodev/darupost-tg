import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, setUnauthorizedHandler, tokenStore } from './api'
import type { ChannelRoleType, CurrentUser } from './types'

interface AuthContextValue {
  user: CurrentUser | null
  ready: boolean
  login: (email: string, password: string) => Promise<void>
  logout: () => void
  /** Highest privilege the user has on a channel (global owner counts as Owner everywhere). */
  roleOn: (channelId: string) => ChannelRoleType | null
  /** True if user meets at least the given role on the channel. */
  canOn: (channelId: string, minimum: ChannelRoleType) => boolean
}

const rank: Record<ChannelRoleType, number> = { Owner: 3, ChannelAdmin: 2, Moderator: 1 }

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [ready, setReady] = useState(false)

  const logout = useCallback(() => {
    tokenStore.clear()
    setUser(null)
  }, [])

  useEffect(() => {
    setUnauthorizedHandler(() => setUser(null))
    if (!tokenStore.get()) {
      setReady(true)
      return
    }
    api.me()
      .then(setUser)
      .catch(() => tokenStore.clear())
      .finally(() => setReady(true))
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const res = await api.login(email, password)
    tokenStore.set(res.token)
    setUser(res.user)
  }, [])

  const roleOn = useCallback((channelId: string): ChannelRoleType | null => {
    if (!user) return null
    if (user.isGlobalOwner) return 'Owner'
    const roles = user.roles.filter((r) => r.channelId === channelId)
    if (roles.length === 0) return null
    return roles.sort((a, b) => rank[b.role] - rank[a.role])[0].role
  }, [user])

  const canOn = useCallback((channelId: string, minimum: ChannelRoleType) => {
    const r = roleOn(channelId)
    return r ? rank[r] >= rank[minimum] : false
  }, [roleOn])

  return (
    <AuthContext.Provider value={{ user, ready, login, logout, roleOn, canOn }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
