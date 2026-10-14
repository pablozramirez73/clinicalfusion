import { useFrame } from '@react-three/fiber';
import { useMemo, useRef, useState } from 'react';
import type { Group } from 'three';
import type { TremorPoint } from '../api/types';
import { normalize, palette } from './colormap';
import { Label } from './Label';
import { t } from '../i18n';

interface Props {
  points: TremorPoint[];
  onSelect?: (sessionId: string) => void;
}

const FREQ_MAX = 14;
const SIZE = 6;

function bandColor(hz: number) {
  if (hz >= 4 && hz <= 6) return palette.rose;   // tipico del tremore parkinsoniano a riposo
  if (hz > 6 && hz < 8) return palette.violet;
  if (hz >= 8 && hz <= 12) return palette.amber; // fisiologico potenziato / essenziale
  return palette.cyan;
}

/**
 * "Galassia" dei tremori: ogni sfera è una rilevazione. X = frequenza dominante, Y = ampiezza RMS,
 * Z = data. Colore = banda di frequenza, dimensione = durata. Ruota lentamente; click → dettaglio.
 */
export function TremorGalaxy3D({ points, onSelect }: Props) {
  const group = useRef<Group>(null);
  const [hover, setHover] = useState<string | null>(null);
  useFrame((_, delta) => {
    if (group.current && !hover) group.current.rotation.y += delta * 0.05;
  });

  const layout = useMemo(() => {
    const maxRms = Math.max(1e-6, ...points.map((p) => p.rmsMs2));
    const times = points.map((p) => Date.parse(p.startedAt));
    const t0 = Math.min(...times), t1 = Math.max(...times);
    return points.map((p, i) => ({
      p,
      position: [
        (p.frequencyHz / FREQ_MAX - 0.5) * SIZE,
        (p.rmsMs2 / maxRms) * 3,
        (normalize(times[i] ?? 0, t0, t1) - 0.5) * SIZE,
      ] as [number, number, number],
      radius: 0.06 + Math.min(0.12, p.durationSeconds / 600),
    }));
  }, [points]);

  return (
    <group ref={group}>
      <gridHelper args={[SIZE + 1, 14, '#1e3a5f', '#0f1d33']} />
      {[4, 8, 12].map((hz) => (
        <group key={hz} position={[(hz / FREQ_MAX - 0.5) * SIZE, 0, 0]}>
          <mesh rotation={[0, Math.PI / 2, 0]} position={[0, 1.5, 0]}>
            <planeGeometry args={[SIZE, 3]} />
            <meshBasicMaterial color="#38bdf8" transparent opacity={0.035} depthWrite={false} />
          </mesh>
          <Label position={[0, 0, SIZE / 2 + 0.4]} center className="axis-label">{hz} Hz</Label>
        </group>
      ))}
      <Label position={[-SIZE / 2 - 0.6, 3, 0]} center className="axis-label">{t('ampiezza ↑')}</Label>
      <Label position={[SIZE / 2 + 0.6, 0, -SIZE / 2]} center className="axis-label">{t('tempo →')}</Label>

      {layout.map(({ p, position, radius }) => (
        <group key={p.sessionId} position={position}>
          <mesh
            onPointerOver={(e) => { e.stopPropagation(); setHover(p.sessionId); }}
            onPointerOut={() => setHover(null)}
            onClick={() => onSelect?.(p.sessionId)}
          >
            <sphereGeometry args={[radius, 24, 24]} />
            <meshStandardMaterial color={bandColor(p.frequencyHz)} emissive={bandColor(p.frequencyHz)} emissiveIntensity={hover === p.sessionId ? 3 : 1.2} />
          </mesh>
          {/* Asta verticale verso il pavimento: aiuta a leggere l'altezza in prospettiva. */}
          <mesh position={[0, -position[1] / 2, 0]}>
            <cylinderGeometry args={[0.004, 0.004, position[1], 6]} />
            <meshBasicMaterial color={bandColor(p.frequencyHz)} transparent opacity={0.35} />
          </mesh>
          {hover === p.sessionId && (
            <Label position={[0, radius + 0.25, 0]} center className="tooltip">
              {p.patientCode} · {p.frequencyHz.toFixed(1)} Hz · {p.rmsMs2.toFixed(3)} m/s²
            </Label>
          )}
        </group>
      ))}
    </group>
  );
}
