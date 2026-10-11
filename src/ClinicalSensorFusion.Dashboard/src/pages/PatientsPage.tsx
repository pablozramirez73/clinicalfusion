import { useDeferredValue, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { usePatients } from '../api/hooks';
import { ErrorBox, Loading, Panel } from '../components/Feedback';
import { age, fmtDate, fullName } from '../components/format';

const sexLabel = { Female: 'F', Male: 'M', Other: 'Altro', Unknown: '—' } as const;

export default function PatientsPage() {
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [placeholders, setPlaceholders] = useState(false);
  // Valore differito: la ricerca non parte a ogni tasto se il rendering è lento.
  const deferred = useDeferredValue(search);
  const { data, error, isPending, isFetching } = usePatients(deferred, page, placeholders);
  const navigate = useNavigate();
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Anagrafica pazienti</h1>
          <p className="muted">I pazienti creati automaticamente dall'app hanno solo il codice: completare l'anagrafica.</p>
        </div>
        <Link className="button primary" to="/patients/new">＋ Nuovo paziente</Link>
      </header>

      <Panel
        title={data ? `${data.total} pazienti` : 'Pazienti'}
        actions={
          <div className="toolbar">
            <input
              type="search"
              placeholder="Cerca codice, cognome, nome, codice fiscale…"
              value={search}
              onChange={(e) => { setSearch(e.target.value); setPage(1); }}
              aria-label="Cerca pazienti"
            />
            <label className="check">
              <input type="checkbox" checked={placeholders} onChange={(e) => { setPlaceholders(e.target.checked); setPage(1); }} />
              Solo da completare
            </label>
          </div>
        }
      >
        {isPending ? <Loading /> : error ? <ErrorBox error={error} /> : (
          <>
            <table className={`table ${isFetching ? 'table--stale' : ''}`}>
              <thead>
                <tr><th>Codice</th><th>Cognome e nome</th><th>Età</th><th>Sesso</th><th>Rilevazioni</th><th>Ultima</th></tr>
              </thead>
              <tbody>
                {data.items.map((p) => (
                  <tr key={p.id} className="clickable" onClick={() => navigate(`/patients/${p.id}`)}>
                    <td><code>{p.code}</code></td>
                    <td>{fullName(p) ?? <span className="chip chip--warn">da completare</span>}</td>
                    <td>{age(p.birthDate) ?? '—'}</td>
                    <td>{sexLabel[p.sex]}</td>
                    <td>{p.sessionCount}</td>
                    <td>{fmtDate(p.lastSessionAt)}</td>
                  </tr>
                ))}
                {data.items.length === 0 && (
                  <tr><td colSpan={6} className="muted">Nessun paziente trovato.</td></tr>
                )}
              </tbody>
            </table>
            <div className="pager">
              <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>‹ Precedente</button>
              <span className="muted">Pagina {page} di {pages}</span>
              <button disabled={page >= pages} onClick={() => setPage((p) => p + 1)}>Successiva ›</button>
            </div>
          </>
        )}
      </Panel>
    </div>
  );
}
