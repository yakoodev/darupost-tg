import type { ButtonHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react'

export function PageHead({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="page-head">
      <div style={{ flex: 1 }}>
        <h2>{title}</h2>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {actions}
    </div>
  )
}

export function Card({ title, subtitle, actions, children, className }: {
  title?: string; subtitle?: string; actions?: ReactNode; children: ReactNode; className?: string
}) {
  return (
    <div className={`card ${className ?? ''}`}>
      {(title || actions) && (
        <div className="panel-head">
          <div>
            {title && <h2>{title}</h2>}
            {subtitle && <div className="sub">{subtitle}</div>}
          </div>
          <div className="spacer" />
          {actions}
        </div>
      )}
      {children}
    </div>
  )
}

type BtnProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'default' | 'primary' | 'success' | 'danger' | 'ghost'
  size?: 'sm' | 'md'
  loading?: boolean
}
export function Button({ variant = 'default', size = 'md', loading, children, className, disabled, ...rest }: BtnProps) {
  return (
    <button
      className={`btn ${variant === 'default' ? '' : variant} ${size === 'sm' ? 'sm' : ''} ${className ?? ''}`}
      disabled={disabled || loading}
      {...rest}
    >
      {loading && <span className="spinner" />}
      {children}
    </button>
  )
}

export function Field({ label, hint, children }: { label?: string; hint?: string; children: ReactNode }) {
  return (
    <div className="field">
      {label && <label>{label}</label>}
      {children}
      {hint && <span className="hint">{hint}</span>}
    </div>
  )
}

export function TextInput(props: React.InputHTMLAttributes<HTMLInputElement>) {
  return <input className="input" {...props} />
}

export function Textarea(props: React.TextareaHTMLAttributes<HTMLTextAreaElement>) {
  return <textarea className="textarea" {...props} />
}

export function Select({ children, ...rest }: SelectHTMLAttributes<HTMLSelectElement> & { children: ReactNode }) {
  return <select className="select" {...rest}>{children}</select>
}

export function Switch({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label className="switch-row" style={{ cursor: 'pointer' }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>{label}</span>
    </label>
  )
}

export function Badge({ tone = '', children }: { tone?: string; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>
}

export function Stat({ label, value, accent }: { label: string; value: ReactNode; accent?: boolean }) {
  return (
    <div className="stat">
      <div className="label">{label}</div>
      <div className={`value ${accent ? 'accent' : ''}`}>{value}</div>
    </div>
  )
}
