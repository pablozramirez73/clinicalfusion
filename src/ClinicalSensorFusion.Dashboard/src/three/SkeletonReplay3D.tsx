import { Line, Trail } from '@react-three/drei';
import { useFrame } from '@react-three/fiber';
import { useMemo, useRef } from 'react';
import { Vector3 } from 'three';
import type { Mesh } from 'three';
import type { PoseReplay } from '../api/types';
import { turbo } from './colormap';
import { Label } from './Label';

/** Indici MediaPipe Pose (vedi Core/Fusion/PoseLandmark.cs). */
const RIGHT_KNEE = 26;
const RIGHT_WRIST = 16;
const LEFT_WRIST = 15;

interface Props {
  replay: PoseReplay;
  /** Indice del fotogramma corrente (controllato dal genitore per la timeline). */
  frame: number;
}

/**
 * Converte un punto dal sistema camera dell'app (X destra, Y giù, Z avanti, metri) al sistema Three.js
 * (Y su), centrato sul bacino del primo fotogramma valido e appoggiato al pavimento.
 */
function useTransform(replay: PoseReplay) {
  return useMemo(() => {
    let cx = 0, cz = 0, floor = -Infinity;
    const first = replay.frames.find((f) => f.points[23 * 3] != null && f.points[24 * 3] != null);
    if (first) {
      cx = ((first.points[23 * 3] ?? 0) + (first.points[24 * 3] ?? 0)) / 2;
      cz = ((first.points[23 * 3 + 2] ?? 0) + (first.points[24 * 3 + 2] ?? 0)) / 2;
    }
    for (const f of replay.frames) {
      for (let i = 1; i < f.points.length; i += 3) {
        const y = f.points[i];
        if (y != null && y > floor) floor = y;
      }
    }
    if (!Number.isFinite(floor)) floor = 0;
    return (x: number, y: number, z: number) => new Vector3(x - cx, floor - y, -(z - cz));
  }, [replay]);
}

export function SkeletonReplay3D({ replay, frame }: Props) {
  const toScene = useTransform(replay);
  const current = replay.frames[Math.min(frame, replay.frames.length - 1)];

  const points = useMemo(() => {
    const result: (Vector3 | null)[] = [];
    if (!current) return result;
    for (let i = 0; i < current.points.length; i += 3) {
      const [x, y, z] = [current.points[i], current.points[i + 1], current.points[i + 2]];
      result.push(x == null || y == null || z == null ? null : toScene(x, y, z));
    }
    return result;
  }, [current, toScene]);

  // Colore dei segmenti in base alla flessione del ginocchio dx (0° blu → 120° rosso): lettura immediata dello sforzo.
  const kneeAngle = current?.angles[7] ?? 0;
  const [r, g, b] = turbo((kneeAngle ?? 0) / 120);
  const boneColor = `rgb(${Math.round(r * 255)}, ${Math.round(g * 255)}, ${Math.round(b * 255)})`;

  return (
    <group>
      <gridHelper args={[6, 30, '#1e3a5f', '#0f1d33']} />
      <mesh rotation={[-Math.PI / 2, 0, 0]} position={[0, -0.001, 0]}>
        <circleGeometry args={[1.4, 64]} />
        <meshStandardMaterial color="#0b1830" emissive="#22d3ee" emissiveIntensity={0.06} transparent opacity={0.8} />
      </mesh>

      {replay.connections.map(([a, c], i) => {
        const pa = points[a];
        const pc = points[c];
        return pa && pc ? <Line key={i} points={[pa, pc]} color={boneColor} lineWidth={3} /> : null;
      })}

      {points.map((p, i) =>
        p ? (
          <mesh key={i} position={p}>
            <sphereGeometry args={[i === RIGHT_KNEE ? 0.045 : 0.028, 16, 16]} />
            <meshStandardMaterial color="#e0f2fe" emissive={i === RIGHT_KNEE ? '#fbbf24' : '#22d3ee'} emissiveIntensity={i === RIGHT_KNEE ? 2.2 : 0.9} />
          </mesh>
        ) : null,
      )}

      {/* Scie luminose dei polsi: traiettoria del movimento degli arti superiori. */}
      <WristTrail target={points[RIGHT_WRIST]} color="#a78bfa" />
      <WristTrail target={points[LEFT_WRIST]} color="#2dd4bf" />

      {points[RIGHT_KNEE] && kneeAngle != null && (
        <Label position={points[RIGHT_KNEE]!.clone().add(new Vector3(0.25, 0.1, 0))} className="tooltip tooltip--small">
          ginocchio dx {Math.round(kneeAngle)}°
        </Label>
      )}
    </group>
  );
}

function WristTrail({ target, color }: { target: Vector3 | null | undefined; color: string }) {
  const ref = useRef<Mesh>(null);
  useFrame(() => {
    if (ref.current && target) ref.current.position.copy(target);
  });
  return (
    <Trail width={0.6} length={6} color={color} attenuation={(t) => t * t}>
      {/* Posizione iniziale sul polso: altrimenti la scia parte dall'origine della scena. */}
      <mesh ref={ref} position={target ?? undefined}>
        <sphereGeometry args={[0.02, 8, 8]} />
        <meshBasicMaterial color={color} />
      </mesh>
    </Trail>
  );
}
