import { Color } from 'three';

/**
 * Colormap "Turbo" (Google, approssimazione polinomiale): percettivamente più uniforme di "jet".
 * È la stessa usata dallo shader Metal dell'app per la mappa LiDAR: stessi colori su telefono e dashboard.
 */
export function turbo(x: number): [number, number, number] {
  const t = Math.min(1, Math.max(0, x));
  const t2 = t * t, t3 = t2 * t, t4 = t3 * t, t5 = t4 * t;
  const r = 0.13572138 + 4.6153926 * t - 42.66032258 * t2 + 132.13108234 * t3 - 152.94239396 * t4 + 59.28637943 * t5;
  const g = 0.09140261 + 2.19418839 * t + 4.84296658 * t2 - 14.18503333 * t3 + 4.27729857 * t4 + 2.82956604 * t5;
  const b = 0.1066733 + 12.64194608 * t - 60.58204836 * t2 + 110.36276771 * t3 - 89.90310912 * t4 + 27.34824973 * t5;
  const clamp = (v: number) => Math.min(1, Math.max(0, v));
  return [clamp(r), clamp(g), clamp(b)];
}

export const turboColor = (x: number) => new Color(...turbo(x));

/** Palette categorica per le modalità di acquisizione e i gruppi articolari. */
export const palette = {
  cyan: '#22d3ee',
  teal: '#2dd4bf',
  violet: '#a78bfa',
  amber: '#fbbf24',
  rose: '#fb7185',
  lime: '#a3e635',
  slate: '#64748b',
};

export const modeColor = { BodyLidar: palette.cyan, HandsLidar: palette.violet, FaceTrueDepth: palette.rose } as const;

/** Normalizza in [0, 1]; con intervallo nullo restituisce 0.5. */
export function normalize(value: number, min: number, max: number): number {
  return max - min < 1e-9 ? 0.5 : (value - min) / (max - min);
}
