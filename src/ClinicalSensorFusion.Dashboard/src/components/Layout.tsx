import type { ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { useLogout } from '../api/hooks';
import type { CurrentUser } from '../api/types';
import { t } from '../i18n';
import { LanguageSelect } from '../i18n/LanguageSelect';

export function Layout({ user, children }: { user: CurrentUser; children: ReactNode }) {
  const logout = useLogout();
  const navigate = useNavigate();
  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <img src="/favicon.svg" alt="" width={36} height={36} />
          <div>
            <strong>Clinical</strong>
            <span>Sensor Fusion</span>
          </div>
        </div>
        <nav>
          <NavLink to="/" end>{t('◈ Panoramica')}</NavLink>
          <NavLink to="/patients">{t('☰ Pazienti')}</NavLink>
          <NavLink to="/patients/new">{t('＋ Nuovo paziente')}</NavLink>
          <NavLink to="/system">{t('♥ Stato del sistema')}</NavLink>
        </nav>
        <div className="sidebar-footer">
          <LanguageSelect />
          <span className="muted">{user.displayName}</span>
          <button
            className="link"
            onClick={() => logout.mutate(undefined, { onSettled: () => navigate('/login', { replace: true }) })}
          >
            {t('Esci')}
          </button>
        </div>
      </aside>
      <main className="content">{children}</main>
    </div>
  );
}
