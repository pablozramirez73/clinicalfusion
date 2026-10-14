import type { ReactNode } from 'react';
import { t } from '../i18n';

export function Loading({ full = false }: { full?: boolean }) {
  return (
    <div className={full ? 'loading loading--full' : 'loading'} role="status" aria-live="polite">
      <span className="pulse" /> {t('Caricamento…')}
    </div>
  );
}

export function ErrorBox({ error }: { error: unknown }) {
  const message = error instanceof Error ? error.message : t('Errore inatteso.');
  return <div className="alert" role="alert">{message}</div>;
}

export function Kpi({ label, value, hint, accent }: { label: string; value: ReactNode; hint?: string; accent?: string }) {
  return (
    <div className="kpi" style={accent ? ({ '--accent': accent } as React.CSSProperties) : undefined}>
      <span className="kpi-label">{label}</span>
      <span className="kpi-value">{value}</span>
      {hint && <span className="kpi-hint">{hint}</span>}
    </div>
  );
}

export function Panel({ title, subtitle, children, actions }: { title: string; subtitle?: string; children: ReactNode; actions?: ReactNode }) {
  return (
    <section className="panel">
      <header className="panel-header">
        <div>
          <h2>{title}</h2>
          {subtitle && <p className="muted">{subtitle}</p>}
        </div>
        {actions}
      </header>
      {children}
    </section>
  );
}

export function Legend({ items }: { items: [string, string][] }) {
  return (
    <ul className="legend">
      {items.map(([label, color]) => (
        <li key={label}><span style={{ background: color }} />{label}</li>
      ))}
    </ul>
  );
}
