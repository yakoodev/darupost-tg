import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import './index.css'
import { AuthProvider, useAuth } from './lib/auth'
import { AppDataProvider } from './lib/appData'
import { ToastProvider } from './lib/toast'
import Layout from './components/Layout'
import Login from './pages/Login'
import Dashboard from './pages/Dashboard'
import Queue from './pages/Queue'
import Channels from './pages/Channels'
import Sources from './pages/Sources'
import PublicationTypes from './pages/PublicationTypes'
import Prompts from './pages/Prompts'
import Schedule from './pages/Schedule'
import History from './pages/History'
import Costs from './pages/Costs'
import Users from './pages/Users'
import Integrations from './pages/Integrations'

function Root() {
  const { user, ready } = useAuth()

  if (!ready) {
    return <div className="loading-screen"><span className="spinner" /><span>Загрузка…</span></div>
  }
  if (!user) return <Login />

  return (
    <AppDataProvider>
      <BrowserRouter>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<Dashboard />} />
            <Route path="queue" element={<Queue />} />
            <Route path="channels" element={<Channels />} />
            <Route path="sources" element={<Sources />} />
            <Route path="types" element={<PublicationTypes />} />
            <Route path="prompts" element={<Prompts />} />
            <Route path="schedule" element={<Schedule />} />
            <Route path="history" element={<History />} />
            <Route path="costs" element={<Costs />} />
            <Route path="users" element={user.isGlobalOwner ? <Users /> : <Navigate to="/" replace />} />
            <Route path="integrations" element={<Integrations />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </AppDataProvider>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider>
      <ToastProvider>
        <Root />
      </ToastProvider>
    </AuthProvider>
  </StrictMode>,
)
