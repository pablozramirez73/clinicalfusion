import { useMemo } from 'react';
import { BufferAttribute, DoubleSide, PlaneGeometry } from 'three';
import type { Spectrogram } from '../api/types';
import { turbo } from './colormap';
import { Label } from './Label';
import { t } from '../i18n';

/**
 * Scala in dB FISSA, uguale per tutte le rilevazioni: così due sessioni sono confrontabili a colpo d'occhio
 * (una scala adattiva farebbe sembrare "alta" anche una sessione di solo rumore). L'intervallo copre dal
 * rumore dell'IMU dell'iPhone (≈ −65 dB) a un tremore marcato (≈ −15 dB) con le unità del server.
 */
export const SPECTROGRAM_DB_RANGE = { min: -65, max: -15 } as const;

interface Props {
  data: Spectrogram;
  width?: number;
  depth?: number;
  height?: number;
}

/**
 * Spettrogramma come superficie 3D: asse X = frequenza, asse Z = tempo (verso l'osservatore), altezza e
 * colore = potenza. Un tremore è una cresta continua parallela all'asse del tempo; le bande 4–6 Hz
 * (parkinsoniano) e 8–12 Hz (fisiologico potenziato / essenziale) sono evidenziate.
 */
export function Spectrogram3D({ data, width = 8, depth = 6, height = 2.4 }: Props) {
  const geometry = useMemo(() => {
    const freqs = data.frequencies.length;
    const times = data.times.length;
    const geo = new PlaneGeometry(width, depth, Math.max(1, freqs - 1), Math.max(1, times - 1));
    geo.rotateX(-Math.PI / 2);

    const { min, max } = SPECTROGRAM_DB_RANGE;
    const position = geo.getAttribute('position') as BufferAttribute;
    const colors = new Float32Array(position.count * 3);
    // Vertici riga per riga: riga = istante (la prima è la più lontana), colonna = frequenza.
    for (let t = 0; t < times; t++) {
      for (let f = 0; f < freqs; f++) {
        const idx = t * freqs + f;
        const v = Math.min(1, Math.max(0, ((data.powerDb[t]?.[f] ?? min) - min) / (max - min)));
        position.setY(idx, v * v * height);
        const [r, g, b] = turbo(v);
        colors.set([r, g, b], idx * 3);
      }
    }
    geo.setAttribute('color', new BufferAttribute(colors, 3));
    geo.computeVertexNormals();
    return geo;
  }, [data, width, depth, height]);

  if (data.times.length < 2) {
    return <Label center className="empty-3d">{t('Dati IMU insufficienti per lo spettrogramma')}</Label>;
  }

  const fMin = data.frequencies[0] ?? 0;
  const fMax = data.frequencies[data.frequencies.length - 1] ?? 20;
  const xOf = (hz: number) => -width / 2 + ((hz - fMin) / (fMax - fMin || 1)) * width;
  const duration = (data.times[data.times.length - 1] ?? 0) - (data.times[0] ?? 0);

  return (
    <group>
      <mesh geometry={geometry}>
        <meshStandardMaterial vertexColors side={DoubleSide} roughness={0.45} metalness={0.1} emissive="#0b1220" />
      </mesh>
      <mesh geometry={geometry} position={[0, 0.002, 0]}>
        <meshBasicMaterial wireframe color="#22d3ee" transparent opacity={0.06} />
      </mesh>

      <Band from={4} to={6} x={xOf} depth={depth} color="#fb7185" label="4–6 Hz" />
      <Band from={8} to={12} x={xOf} depth={depth} color="#fbbf24" label="8–12 Hz" />

      {[2, 5, 10, 15, 20].filter((f) => f >= fMin && f <= fMax).map((f) => (
        <Label key={f} position={[xOf(f), 0, depth / 2 + 0.45]} center className="axis-label">{f} Hz</Label>
      ))}
      <Label position={[width / 2 + 0.5, 0, 0]} className="axis-label">tempo ↓ {duration.toFixed(0)} s</Label>
      <Label position={[width / 2 + 0.3, height, -depth / 2]} className="axis-label">
        scala fissa {SPECTROGRAM_DB_RANGE.min}…{SPECTROGRAM_DB_RANGE.max} dB
      </Label>
    </group>
  );
}

function Band({ from, to, x, depth, color, label }: { from: number; to: number; x: (hz: number) => number; depth: number; color: string; label: string }) {
  const center = (x(from) + x(to)) / 2;
  const size = Math.abs(x(to) - x(from));
  return (
    <group>
      <mesh position={[center, 0.01, 0]} rotation={[-Math.PI / 2, 0, 0]}>
        <planeGeometry args={[size, depth]} />
        <meshBasicMaterial color={color} transparent opacity={0.1} depthWrite={false} />
      </mesh>
      <Label position={[center, 0.05, -depth / 2 - 0.35]} center className="axis-label" style={{ color }}>{label}</Label>
    </group>
  );
}
