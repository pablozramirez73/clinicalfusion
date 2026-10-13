import { describe, expect, it } from 'vitest';
import { en } from './en';

// Tutti i sorgenti della dashboard come testo (esclusi i test e il catalogo stesso).
const sources = import.meta.glob<string>('../**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true });

/** Testi passati a t() o n() in tutto il codice della dashboard. */
function usedTexts(): Set<string> {
  const used = new Set<string>();
  for (const [path, source] of Object.entries(sources)) {
    if (path.endsWith('.test.ts') || path.endsWith('/en.ts')) continue;
    for (const match of source.matchAll(/\b[tn]\('((?:[^'\\]|\\.)*)'/g)) {
      used.add(match[1]!.replace(/\\'/g, "'"));
    }
  }
  return used;
}

describe('catalogo inglese', () => {
  const used = usedTexts();

  it('traduce ogni testo dell\'interfaccia', () => {
    expect(used.size).toBeGreaterThan(100);
    expect([...used].filter((text) => !(text in en))).toEqual([]);
  });

  it('non ha voci che il codice non usa più', () => {
    expect(Object.keys(en).filter((text) => !used.has(text))).toEqual([]);
  });

  it('conserva i segnaposto', () => {
    const placeholders = (text: string) => (text.match(/\{\d+\}/g) ?? []).sort().join();
    expect(Object.entries(en).filter(([it, translated]) => placeholders(it) !== placeholders(translated))).toEqual([]);
  });
});
