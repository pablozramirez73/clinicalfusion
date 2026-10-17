import { useAccounts, useDecideAccount } from '../api/hooks';
import type { AccountItem, AccountStatus } from '../api/types';
import { ErrorBox, Loading, Panel } from '../components/Feedback';
import { fmtDateTime } from '../components/format';
import { n, t } from '../i18n';

const statusLabel: Record<AccountStatus, string> = {
  Pending: n('In attesa'), Approved: n('Approvato'), Rejected: n('Rifiutato'), Disabled: n('Disattivato'),
};
const statusColor: Record<AccountStatus, string> = {
  Pending: 'var(--amber)', Approved: 'var(--ok)', Rejected: 'var(--rose)', Disabled: 'var(--muted)',
};

/** Richieste di accesso con Google e account registrati: l'amministratore approva, rifiuta o toglie l'accesso. */
export default function AccountsPage() {
  const accounts = useAccounts();
  const decide = useDecideAccount();

  if (accounts.isPending) return <Loading />;
  if (accounts.error) return <ErrorBox error={accounts.error} />;

  const pending = accounts.data.filter((a) => a.status === 'Pending');
  const others = accounts.data.filter((a) => a.status !== 'Pending');

  const actions = (a: AccountItem) => (
    <div className="row-actions">
      {a.status !== 'Approved' && (
        <>
          <button className="primary" disabled={decide.isPending} onClick={() => decide.mutate({ id: a.id, action: 'approve' })}>
            {t('Approva')}
          </button>
          <button disabled={decide.isPending} onClick={() => decide.mutate({ id: a.id, action: 'approve', isAdmin: true })}>
            {t('Approva come amministratore')}
          </button>
        </>
      )}
      {a.status === 'Pending' && (
        <button className="danger" disabled={decide.isPending} onClick={() => decide.mutate({ id: a.id, action: 'reject' })}>
          {t('Rifiuta')}
        </button>
      )}
      {a.status === 'Approved' && (
        <button className="danger" disabled={decide.isPending} onClick={() => decide.mutate({ id: a.id, action: 'disable' })}>
          {t('Togli l\'accesso')}
        </button>
      )}
    </div>
  );

  const rows = (list: AccountItem[], decided: boolean) => (
    <table className="table">
      <thead>
        <tr>
          <th>{t('Account Google')}</th>
          <th>{t('Stato')}</th>
          <th>{decided ? t('Deciso') : t('Richiesto')}</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {list.map((a) => (
          <tr key={a.id}>
            <td>
              <strong>{a.displayName ?? a.email}</strong>
              <div className="muted">{a.email}{a.isAdmin && ` · ${t('amministratore')}`}</div>
            </td>
            <td><span className="chip" style={{ '--chip': statusColor[a.status] } as React.CSSProperties}>{t(statusLabel[a.status])}</span></td>
            <td className="muted">
              {decided && a.decidedAt ? `${fmtDateTime(a.decidedAt)}${a.decidedBy ? ` · ${a.decidedBy}` : ''}` : fmtDateTime(a.requestedAt)}
            </td>
            <td>{actions(a)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>{t('Accessi')}</h1>
          <p className="muted">{t('Chi si registra con Google non vede nessun dato finché non lo approvi.')}</p>
        </div>
      </header>
      {decide.error && <ErrorBox error={decide.error} />}
      <Panel title={t('Richieste in attesa')} subtitle={pending.length === 0 ? t('Nessuna richiesta da decidere.') : undefined}>
        {pending.length > 0 && rows(pending, false)}
      </Panel>
      <Panel title={t('Account')}>
        {others.length === 0 ? <p className="muted">{t('Nessun account registrato con Google.')}</p> : rows(others, true)}
      </Panel>
    </div>
  );
}
