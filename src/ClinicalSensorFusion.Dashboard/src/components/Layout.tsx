import type { ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { useLogout } from '../api/hooks';
import type { CurrentUser } from '../api/types';

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
          <NavLink to="/" end>◈ Panoramica</NavLink>
          <NavLink to="/patients">☰ Pazienti</NavLink>
          <NavLink to="/patients/new">＋ Nuovo paziente</NavLink>
        </nav>
        <div className="sidebar-footer">
          <span className="muted">{user.displayName}</span>
          <button
            className="link"
            onClick={() => logout.mutate(undefined, { onSettled: () => navigate('/login', { replace: true }) })}
          >
            Esci
          </button>
        </div>
      </aside>
      <main className="content">{children}</main>
    </div>
  );
}
