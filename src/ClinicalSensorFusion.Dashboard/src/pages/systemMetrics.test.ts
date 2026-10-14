import { describe, expect, it } from 'vitest';
import type { MetricPoint } from '../api/types';
import { counterTotal, fmtBytes, fmtUptime, parseTags, routeStats } from './systemMetrics';

const point = (name: string, tags: string, count: number, sum: number, max = 0): MetricPoint =>
  ({ name, unit: null, kind: 'histogram', tags, count, sum, min: 0, max, last: 0, mean: count ? sum / count : 0 });

describe('system metrics', () => {
  it('parses tags, keeping "=" inside values', () => {
    expect(parseTags('http.route=/api/v1/x,outcome=success')).toEqual({ 'http.route': '/api/v1/x', outcome: 'success' });
    expect(parseTags('')).toEqual({});
    expect(parseTags('k=a=b')).toEqual({ k: 'a=b' });
  });

  it('merges request series into one row per route with errors and latency', () => {
    const rows = routeStats([
      point('http.server.request.duration', 'http.request.method=GET,http.response.status_code=200,http.route=/api/v1/patients', 8, 0.4, 0.2),
      point('http.server.request.duration', 'http.request.method=GET,http.response.status_code=500,http.route=/api/v1/patients', 2, 0.6, 0.5),
      point('http.server.request.duration', 'http.request.method=POST,http.response.status_code=401,http.route=/api/v1/auth/login', 3, 0.03, 0.02),
      point('csf.ingest.chunks', 'result=inserted', 1, 10),
    ]);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({ route: 'GET /api/v1/patients', requests: 10, serverErrors: 2, clientErrors: 0, maxMs: 500 });
    expect(rows[0]!.meanMs).toBeCloseTo(100);
    expect(rows[1]).toMatchObject({ route: 'POST /api/v1/auth/login', clientErrors: 3 });
  });

  it('sums counters filtered by attributes', () => {
    const points = [
      point('csf.ingest.chunks', 'result=inserted', 2, 40),
      point('csf.ingest.chunks', 'result=rejected', 1, 3),
    ];
    expect(counterTotal(points, 'csf.ingest.chunks')).toBe(43);
    expect(counterTotal(points, 'csf.ingest.chunks', { result: 'rejected' })).toBe(3);
    expect(counterTotal(points, 'csf.missing')).toBe(0);
  });

  it('formats uptime and sizes', () => {
    expect(fmtUptime(125)).toBe('2 min');
    expect(fmtUptime(3 * 3600 + 120)).toBe('3 h 2 min');
    expect(fmtUptime(2 * 86400 + 3600)).toBe('2 g 1 h');
    expect(fmtBytes(512)).toBe('512 B');
    expect(fmtBytes(5 * 1024 ** 3)).toBe('5.0 GB');
  });
});
