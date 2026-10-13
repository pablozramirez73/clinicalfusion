import { useSyncExternalStore } from 'react';
import { en } from './en';

/**
 * Traduzione dei testi della dashboard. Come nell'app, la chiave è il testo italiano così com'è scritto nel
 * codice: l'italiano non ha bisogno di un catalogo e una traduzione mancante ricade sull'italiano.
 * I segnaposto sono {0}, {1}, …
 */
export type Language = 'it' | 'en';

export const languages: { code: Language; name: string }[] = [
  { code: 'it', name: 'Italiano' },
  { code: 'en', name: 'English' },
];

const catalogs: Record<Language, Record<string, string> | null> = { it: null, en };
const storageKey = 'csf.language';
const listeners = new Set<() => void>();

function initial(): Language {
  try {
    const saved = window.localStorage.getItem(storageKey);
    if (saved === 'it' || saved === 'en') return saved;
  } catch {
    // Archivio del browser non disponibile (navigazione privata): si usa la lingua del browser.
  }
  // Senza una scelta salvata: la lingua del browser se è l'italiano, altrimenti l'inglese.
  return typeof navigator !== 'undefined' && navigator.language?.toLowerCase().startsWith('it') ? 'it' : 'en';
}

let current: Language = typeof window === 'undefined' ? 'it' : initial();
if (typeof document !== 'undefined') document.documentElement.lang = current;

export const getLanguage = () => current;

export function setLanguage(language: Language) {
  if (language === current) return;
  current = language;
  try {
    window.localStorage.setItem(storageKey, language);
  } catch {
    // Vale comunque per questa sessione.
  }
  document.documentElement.lang = language;
  listeners.forEach((notify) => notify());
}

/** Lingua corrente; il componente si aggiorna quando cambia. */
export function useLanguage(): Language {
  return useSyncExternalStore(
    (notify) => {
      listeners.add(notify);
      return () => listeners.delete(notify);
    },
    getLanguage,
  );
}

/** Impostazioni locali per date e numeri nella lingua corrente. */
export const locale = () => (current === 'it' ? 'it-IT' : 'en-GB');

/** Traduce nella lingua corrente e sostituisce i segnaposto {0}, {1}, … */
export function t(italian: string, ...args: (string | number)[]): string {
  const text = catalogs[current]?.[italian] ?? italian;
  return args.length === 0 ? text : text.replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? ''));
}

/**
 * Marca un testo da tradurre senza tradurlo: per le tabelle costanti (nomi delle modalità, delle
 * articolazioni), che si traducono con t() quando si mostrano. Il test di completezza le trova come t().
 */
export const n = (italian: string) => italian;
