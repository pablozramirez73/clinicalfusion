import { useSystemHealth, useSystemMetrics } from '../api/hooks';
import type { HealthStatus } from '../api/types';
import { ErrorBox, Kpi, Loading, Panel } from '../components/Feedback';
import { fmtDateTime } from '../components/format';
import { counterTotal, fmtBytes, fmtUptime, routeStats } from './systemMetrics';
import { locale, n, t } from '../i18n';

const statusLabel: Record<HealthStatus, string> = { Healthy: n('Operativo'), Degraded: n('Degradato'), Unhealthy: n('Non operativo') };
const statusColor: Record<HealthStatus, string> = { Healthy: 'var(--ok)', Degraded: 'var(--amber)', Unhealthy: 'var(--rose)' };
const checkLabel: Record<string, string> = { database: n('Database PostgreSQL'), 'audio-storage': n('Archivio audio') };

function StatusChip({ status }: { status: HealthStatus }) {
  return <span className="chip" style={{ '--chip': statusColor[status] } as React.CSSProperties}>{t(statusLabel[status])}</span>;
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
          <h1>{t('Stato del sistema')}</h1>
          <p className="muted">
            {t('Versione {0} · ambiente {1} · avviato {2} · aggiornamento ogni 15 s', h.version, h.environment, fmtDateTime(h.startedAt))}
          </p>
        </div>
        <StatusChip status={h.status} />
      </header>

      <div className="kpis">
        <Kpi label={t('Stato')} value={t(statusLabel[h.status])} hint={t('attivo da {0}', fmtUptime(h.uptimeSeconds))} accent={statusColor[h.status]} />
        <Kpi label={t('Richieste API')} value={requests.toLocaleString(locale())} hint={t('dall\'avvio')} />
        <Kpi
          label={t('Errori del server')}
          value={serverErrors.toLocaleString(locale())}
          hint={requests ? t('{0}% delle richieste', ((serverErrors / requests) * 100).toFixed(2)) : t('nessuna richiesta')}
          accent={serverErrors > 0 ? 'var(--rose)' : undefined}
        />
        <Kpi label={t('Memoria')} value={`${h.workingSetMb.toFixed(0)} MB`} hint={t('heap gestito {0} MB · {1} thread', h.gcHeapMb.toFixed(0), h.threadCount)} />
      </div>

      <div className="grid-2">
        <Panel title={t('Controlli di salute')} subtitle={t('Dipendenze senza le quali il servizio non può ricevere o mostrare i dati.')}>
          <table className="table">
            <thead><tr><th>{t('Controllo')}</th><th>{t('Stato')}</th><th>{t('Dettaglio')}</th><th>{t('Tempo')}</th></tr></thead>
            <tbody>
              {h.checks.map((c) => (
                <tr key={c.name}>
                  <td>{t(checkLabel[c.name] ?? c.name)}</td>
                  <td><StatusChip status={c.status} /></td>
                  <td className="muted">
                    {c.description}
                    {typeof c.data.freeBytes === 'number' && ` ${t('Spazio libero: {0}.', fmtBytes(c.data.freeBytes))}`}
                  </td>
                  <td>{c.durationMs.toFixed(0)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>

        <Panel title={t('Ingestione e accessi')} subtitle={t('Dall\'avvio del server. I valori diversi da zero nella seconda riga meritano un controllo nel registro di audit.')}>
          <div className="kpis">
            <Kpi label={t('Rilevazioni ricevute')} value={counterTotal(points, 'csf.ingest.sessions', { result: 'accepted' })} />
            <Kpi label={t('Blocchi salvati')} value={counterTotal(points, 'csf.ingest.chunks', { result: 'inserted' }).toLocaleString(locale())} />
            <Kpi label={t('Dati ricevuti')} value={fmtBytes(counterTotal(points, 'csf.ingest.bytes'))} />
            <Kpi label={t('Login falliti')} value={failedLogins} accent={failedLogins > 0 ? 'var(--amber)' : undefined} />
            <Kpi label={t('Ingestioni rifiutate')} value={rejectedChunks + deniedIngest} hint={t('blocchi non validi o dispositivo non proprietario')} accent={rejectedChunks + deniedIngest > 0 ? 'var(--amber)' : undefined} />
            <Kpi label={t('Richieste limitate (429)')} value={rateLimited} accent={rateLimited > 0 ? 'var(--amber)' : undefined} />
          </div>
        </Panel>
      </div>

      <Panel title={t('Rotte')} subtitle={t('Traffico, errori e latenza per rotta (dall\'avvio).')}>
        {routes.length === 0 ? <p className="muted">{t('Nessuna richiesta registrata.')}</p> : (
          <table className="table">
            <thead><tr><th>{t('Rotta')}</th><th>{t('Richieste')}</th><th>{t('Errori 5xx')}</th><th>{t('Errori 4xx')}</th><th>{t('Latenza media')}</th><th>{t('Latenza massima')}</th></tr></thead>
            <tbody>
              {routes.map((r) => (
                <tr key={r.route}>
                  <td><code>{r.route}</code></td>
                  <td>{r.requests.toLocaleString(locale())}</td>
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
