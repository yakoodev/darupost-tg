import { useEffect, useRef, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { API_BASE } from './api'

export type LiveState = 'connecting' | 'live' | 'offline'

/**
 * Subscribes to the backend SignalR hub and invokes `onChange` (debounced) whenever the server
 * broadcasts a state change. Returns the current connection state for a live indicator.
 */
export function useRealtime(onChange: () => void): LiveState {
  const [state, setState] = useState<LiveState>('connecting')
  const cbRef = useRef(onChange)
  cbRef.current = onChange

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE}/hubs/posts`)
      .withAutomaticReconnect()
      .build()

    let timer: number | undefined
    const debounced = () => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => cbRef.current(), 300)
    }

    connection.on('stateChanged', debounced)
    connection.onreconnecting(() => setState('connecting'))
    connection.onreconnected(() => setState('live'))
    connection.onclose(() => setState('offline'))

    connection.start().then(() => setState('live')).catch(() => setState('offline'))

    return () => {
      window.clearTimeout(timer)
      connection.stop()
    }
  }, [])

  return state
}
