import { describe, expect, it } from 'vitest';
import { normalize, turbo } from './colormap';

describe('turbo', () => {
  it('va dal blu (valori bassi) al rosso (valori alti)', () => {
    const [r0, , b0] = turbo(0.15);
    const [r1, , b1] = turbo(0.9);
    expect(b0).toBeGreaterThan(r0);
    expect(r1).toBeGreaterThan(b1);
  });

  it('limita i valori fuori intervallo', () => {
    expect(turbo(-5)).toEqual(turbo(0));
    expect(turbo(7)).toEqual(turbo(1));
    for (const c of turbo(0.37)) {
      expect(c).toBeGreaterThanOrEqual(0);
      expect(c).toBeLessThanOrEqual(1);
    }
  });
});

describe('normalize', () => {
  it('gestisce intervalli nulli', () => {
    expect(normalize(3, 3, 3)).toBe(0.5);
    expect(normalize(5, 0, 10)).toBe(0.5);
  });
});
