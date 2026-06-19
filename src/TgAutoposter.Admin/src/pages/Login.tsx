import { useState } from 'react'
import { Bot } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { Button, Field, TextInput } from '../components/ui'

export default function Login() {
  const { login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      await login(email.trim(), password)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось войти')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-wrap">
      <form className="login-card" onSubmit={submit}>
        <div className="brand">
          <div className="brand-logo"><Bot size={20} /></div>
        </div>
        <h2>TG Autoposter</h2>
        <div className="sub">Панель управления автопостингом</div>

        {error && <div className="login-error">{error}</div>}

        <Field label="Email">
          <TextInput type="email" value={email} autoFocus autoComplete="username"
            onChange={(e) => setEmail(e.target.value)} placeholder="owner@local" />
        </Field>
        <Field label="Пароль">
          <TextInput type="password" value={password} autoComplete="current-password"
            onChange={(e) => setPassword(e.target.value)} placeholder="••••••••" />
        </Field>
        <Button type="submit" variant="primary" className="block" loading={busy} style={{ marginTop: 8 }}>
          Войти
        </Button>
      </form>
    </div>
  )
}
