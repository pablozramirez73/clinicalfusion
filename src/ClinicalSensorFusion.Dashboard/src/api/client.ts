import { getLanguage, t } from '../i18n';

// Client HTTP minimale: stessa origine, cookie di sessione HttpOnly (mai token in localStorage).

export class ApiError extends Error {
  constructor(public readonly status: number, message: string, public readonly details?: Record<string, string[]>) {
    super(message);
  }
}

let onUnauthorized: (() => void) | null = null;

/** Registrato dall'app: un 401 su qualunque chiamata riporta al login. */
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(`/api/v1/${path}`, {
    ...init,
    credentials: 'same-origin',
    // Il server risponde con i messaggi nella lingua scelta nella dashboard.
    headers: { Accept: 'application/json', 'Accept-Language': getLanguage(), ...(init.body ? { 'Content-Type': 'application/json' } : {}), ...init.headers },
  });

  if (response.status === 401 && !path.startsWith('auth/login')) {
    onUnauthorized?.();
  }

  if (!response.ok) {
    let message = t('Errore {0}', response.status);
    let details: Record<string, string[]> | undefined;
    const text = await response.text();
    try {
      const problem = JSON.parse(text) as { title?: string; detail?: string; errors?: Record<string, string[]> };
      details = problem.errors;
      message = problem.errors ? Object.values(problem.errors).flat().join(' ') : problem.detail ?? problem.title ?? message;
    } catch {
      if (text) message = text.replace(/^"|"$/g, '');
    }
    throw new ApiError(response.status, message, details);
  }

  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const post = <T>(path: string, body?: unknown) => api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });
export const put = <T>(path: string, body: unknown) => api<T>(path, { method: 'PUT', body: JSON.stringify(body) });
export const del = (path: string) => api<void>(path, { method: 'DELETE' });
