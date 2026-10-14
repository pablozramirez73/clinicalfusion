import type { MetricPoint } from '../api/types';

/** Riga della tabella delle rotte: traffico, errori e latenza (i "segnali d'oro") di una rotta HTTP. */
export interface RouteStats { route: string; requests: number; serverErrors: number; clientErrors: number; meanMs: number; maxMs: number }

export function parseTags(tags: string): Record<string, string> {
  const result: Record<string, string> = {};
  for (const part of tags.split(',')) {
    const i = part.indexOf('=');
    if (i > 0) result[part.slice(0, i)] = part.slice(i + 1);
  }
  return result;
}

/** Riunisce le serie di `http.server.request.duration` (una per metodo e stato) in una riga per rotta. */
export function routeStats(points: MetricPoint[]): RouteStats[] {
  const byRoute = new Map<string, RouteStats & { totalSeconds: number }>();
  for (const p of points) {
    if (p.name !== 'http.server.request.duration') continue;
    const tags = parseTags(p.tags);
    const route = `${tags['http.request.method'] ?? '?'} ${tags['http.route'] ?? '(altre)'}`;
    const status = Number(tags['http.response.status_code'] ?? 0);
    const row = byRoute.get(route) ?? { route, requests: 0, serverErrors: 0, clientErrors: 0, meanMs: 0, maxMs: 0, totalSeconds: 0 };
    row.requests += p.count;
    row.totalSeconds += p.sum;
    row.maxMs = Math.max(row.maxMs, p.max * 1000);
    if (status >= 500) row.serverErrors += p.count;
    else if (status >= 400) row.clientErrors += p.count;
    byRoute.set(route, row);
  }
  return [...byRoute.values()]
    .map(({ totalSeconds, ...row }) => ({ ...row, meanMs: row.requests ? (totalSeconds / row.requests) * 1000 : 0 }))
    .sort((a, b) => b.requests - a.requests);
}

/** Totale di un contatore, sommando le serie i cui attributi contengono tutti quelli richiesti. */
export function counterTotal(points: MetricPoint[], name: string, where: Record<string, string> = {}): number {
  let total = 0;
  for (const p of points) {
    if (p.name !== name) continue;
    const tags = parseTags(p.tags);
    if (Object.entries(where).every(([k, v]) => tags[k] === v)) total += p.sum;
  }
  return total;
}

export function fmtUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d > 0) return `${d} g ${h} h`;
  if (h > 0) return `${h} h ${m} min`;
  return `${m} min`;
}

export function fmtBytes(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${bytes} B`;
}
