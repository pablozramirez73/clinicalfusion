import { useSystemHealth, useSystemMetrics } from '../api/hooks';
import type { HealthStatus } from '../api/types';
import { ErrorBox, Kpi, Loading, Panel } from '../components/Feedback';
import { fmtDateTime } from '../components/format';
import { counterTotal, fmtBytes, fmtUptime, routeStats } from './systemMetrics';

const statusLabel: Record<HealthStatus, string> = { Healthy: 'Operativo', Degraded: 'Degradato', Unhealthy: 'Non operativo' };
const statusColor: Record<HealthStatus, string> = { Healthy: 'var(--ok)', Degraded: 'var(--amber)', Unhealthy: 'var(--rose)' };
const checkLabel: Record<string, string> = { database: 'Database PostgreSQL', 'audio-storage': 'Archivio audio' };

function StatusChip({ status }: { status: HealthStatus }) {
  return <span className="chip" style={{ '--chip': statusColor[status] } as React.CSSProperties}>{statusLabel[status]}</span>;
}

/** Stato del servizio e metriche dall'avvio: controlli di salute, traffico/errori/latenza per rotta, ingestione, accessi. */
export default function SystemPage() {
  const health = useSystemHealth();
  const metrics = useSystemMetrics();

  if (health.isPending || metrics.isPending) return <Loading />;
  if (health.error) return <ErrorBox error={health.error} />;
  if (metrics.error) return <ErrorBox error={metrics.error} />;

  const h = health.data;
  const points = metrics.data.points;
  const routes = routeStats(points);
  const requests = routes.reduce((n, r) => n + r.requests, 0);
  const serverErrors = routes.reduce((n, r) => n + r.serverErrors, 0);
  const failedLogins = counterTotal(points, 'csf.auth.logins', { outcome: 'error' });
  const rejectedChunks = counterTotal(points, 'csf.ingest.chunks', { result: 'rejected' });
  const deniedIngest = counterTotal(points, 'csf.ingest.sessions', { result: 'denied' });
  const rateLimited = counterTotal(points, 'csf.ratelimit.rejections');

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Stato del sistema</h1>
          <p className="muted">
            Versione {h.version} · ambiente {h.environment} · avviato {fmtDateTime(h.startedAt)} · aggiornamento ogni 15 s
          </p>
        </div>
        <StatusChip status={h.status} />
      </header>

      <div className="kpis">
        <Kpi label="Stato" value={statusLabel[h.status]} hint={`attivo da ${fmtUptime(h.uptimeSeconds)}`} accent={statusColor[h.status]} />
        <Kpi label="Richieste API" value={requests.toLocaleString('it-IT')} hint="dall'avvio" />
        <Kpi
          label="Errori del server"
          value={serverErrors.toLocaleString('it-IT')}
          hint={requests ? `${((serverErrors / requests) * 100).toFixed(2)}% delle richieste` : 'nessuna richiesta'}
          accent={serverErrors > 0 ? 'var(--rose)' : undefined}
        />
        <Kpi label="Memoria" value={`${h.workingSetMb.toFixed(0)} MB`} hint={`heap gestito ${h.gcHeapMb.toFixed(0)} MB · ${h.threadCount} thread`} />
      </div>

      <div className="grid-2">
        <Panel title="Controlli di salute" subtitle="Dipendenze senza le quali il servizio non può ricevere o mostrare i dati.">
          <table className="table">
            <thead><tr><th>Controllo</th><th>Stato</th><th>Dettaglio</th><th>Tempo</th></tr></thead>
            <tbody>
              {h.checks.map((c) => (
                <tr key={c.name}>
                  <td>{checkLabel[c.name] ?? c.name}</td>
                  <td><StatusChip status={c.status} /></td>
                  <td className="muted">
                    {c.description}
                    {typeof c.data.freeBytes === 'number' && ` Spazio libero: ${fmtBytes(c.data.freeBytes)}.`}
                  </td>
                  <td>{c.durationMs.toFixed(0)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>

        <Panel title="Ingestione e accessi" subtitle="Dall'avvio del server. I valori diversi da zero nella seconda riga meritano un controllo nel registro di audit.">
          <div className="kpis">
            <Kpi label="Rilevazioni ricevute" value={counterTotal(points, 'csf.ingest.sessions', { result: 'accepted' })} />
            <Kpi label="Blocchi salvati" value={counterTotal(points, 'csf.ingest.chunks', { result: 'inserted' }).toLocaleString('it-IT')} />
            <Kpi label="Dati ricevuti" value={fmtBytes(counterTotal(points, 'csf.ingest.bytes'))} />
            <Kpi label="Login falliti" value={failedLogins} accent={failedLogins > 0 ? 'var(--amber)' : undefined} />
            <Kpi label="Ingestioni rifiutate" value={rejectedChunks + deniedIngest} hint="blocchi non validi o dispositivo non proprietario" accent={rejectedChunks + deniedIngest > 0 ? 'var(--amber)' : undefined} />
            <Kpi label="Richieste limitate (429)" value={rateLimited} accent={rateLimited > 0 ? 'var(--amber)' : undefined} />
          </div>
        </Panel>
      </div>

      <Panel title="Rotte" subtitle="Traffico, errori e latenza per rotta (dall'avvio).">
        {routes.length === 0 ? <p className="muted">Nessuna richiesta registrata.</p> : (
          <table className="table">
            <thead><tr><th>Rotta</th><th>Richieste</th><th>Errori 5xx</th><th>Errori 4xx</th><th>Latenza media</th><th>Latenza massima</th></tr></thead>
            <tbody>
              {routes.map((r) => (
                <tr key={r.route}>
                  <td><code>{r.route}</code></td>
                  <td>{r.requests.toLocaleString('it-IT')}</td>
                  <td>{r.serverErrors > 0 ? <span className="chip" style={{ '--chip': 'var(--rose)' } as React.CSSProperties}>{r.serverErrors}</span> : 0}</td>
                  <td>{r.clientErrors}</td>
                  <td>{r.meanMs.toFixed(0)} ms</td>
                  <td>{r.maxMs.toFixed(0)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>
    </div>
  );
}
