import { RoundedBox } from '@react-three/drei';
import { useFrame } from '@react-three/fiber';
import { useMemo, useRef, useState } from 'react';
import type { Group } from 'three';
import { MathUtils } from 'three';
import { Label } from './Label';

export interface Bar {
  key: string;
  x: number;
  z: number;
  value: number;
  color: string;
  tooltip: string;
}

interface Bars3DProps {
  bars: Bar[];
  xLabels: string[];
  zLabels: string[];
  maxHeight?: number;
  /** Distanza tra le colonne (asse X). */
  spacing?: number;
  /** Distanza tra le righe (asse Z); di default uguale a spacing. */
  rowSpacing?: number;
}

/**
 * Istogramma 3D generico (righe × colonne). Le barre "crescono" all'apertura con un'animazione smorzata e
 * mostrano il valore al passaggio del mouse. Usato per attività settimanale e mappa delle escursioni articolari.
 */
export function Bars3D({ bars, xLabels, zLabels, maxHeight = 3.2, spacing = 0.9, rowSpacing = spacing }: Bars3DProps) {
  const max = useMemo(() => Math.max(1e-6, ...bars.map((b) => b.value)), [bars]);
  const width = (xLabels.length - 1) * spacing;
  const depth = (zLabels.length - 1) * rowSpacing;
  const [hover, setHover] = useState<string | null>(null);

  return (
    <group position={[-width / 2, 0, -depth / 2]}>
      <gridHelper args={[Math.max(width, depth) + 3, 24, '#1e3a5f', '#0f1d33']} position={[width / 2, 0, depth / 2]} />
      {bars.map((b) => (
        <GrowingBar
          key={b.key}
          position={[b.x * spacing, 0, b.z * rowSpacing]}
          height={Math.max(0.02, (b.value / max) * maxHeight)}
          color={b.color}
          highlighted={hover === b.key}
          onHover={(on) => setHover(on ? b.key : null)}
          tooltip={b.tooltip}
        />
      ))}
      {xLabels.map((label, i) =>
        label ? (
          <Label key={`x${i}`} position={[i * spacing, 0, depth + 0.9]} center className="axis-label">
            {label}
          </Label>
        ) : null,
      )}
      {zLabels.map((label, i) => (
        <Label key={`z${i}`} position={[width + 0.7, 0, i * rowSpacing]} className="axis-label">
          {label}
        </Label>
      ))}
    </group>
  );
}

interface GrowingBarProps {
  position: [number, number, number];
  height: number;
  color: string;
  highlighted: boolean;
  tooltip: string;
  onHover: (on: boolean) => void;
}

function GrowingBar({ position, height, color, highlighted, tooltip, onHover }: GrowingBarProps) {
  const ref = useRef<Group>(null);
  useFrame((_, delta) => {
    const g = ref.current;
    if (!g) return;
    // Interpolazione esponenziale indipendente dal frame rate.
    g.scale.y = MathUtils.damp(g.scale.y, height, 4, delta);
  });

  return (
    <group position={position}>
      <group ref={ref} scale={[1, 0.001, 1]}>
        <RoundedBox
          args={[0.6, 1, 0.6]}
          radius={0.06}
          position={[0, 0.5, 0]}
          onPointerOver={(e) => { e.stopPropagation(); onHover(true); }}
          onPointerOut={() => onHover(false)}
        >
          <meshStandardMaterial
            color={color}
            emissive={color}
            emissiveIntensity={highlighted ? 1.4 : 0.35}
            metalness={0.3}
            roughness={0.35}
            transparent
            opacity={0.92}
          />
        </RoundedBox>
      </group>
      {highlighted && (
        <Label position={[0, height + 0.4, 0]} center className="tooltip">
          {tooltip}
        </Label>
      )}
    </group>
  );
}
