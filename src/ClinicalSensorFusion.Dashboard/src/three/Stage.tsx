import { Canvas } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import { Bloom, EffectComposer, Vignette } from '@react-three/postprocessing';
import { useRef, type ReactNode } from 'react';
import { LabelLayerContext } from './Label';
import { t } from '../i18n';

interface StageProps {
  children: ReactNode;
  camera?: [number, number, number];
  target?: [number, number, number];
  height?: number;
  autoRotate?: boolean;
  label: string;
}

/**
 * Scena 3D comune: luci, nebbia, controlli orbitali, bloom (i colori più luminosi "brillano") e vignettatura.
 * Nessuna risorsa esterna (HDR, font da CDN): la Content-Security-Policy del server consente solo 'self'.
 */
export function Stage({ children, camera = [6, 5, 8], target = [0, 0, 0], height = 420, autoRotate = false, label }: StageProps) {
  const labels = useRef<HTMLDivElement>(null);
  return (
    <div className="stage" style={{ height }} role="img" aria-label={label}>
      <LabelLayerContext.Provider value={labels}>
      <Canvas camera={{ position: camera, fov: 45 }} dpr={[1, 2]} gl={{ antialias: true }}>
        <color attach="background" args={['#060a13']} />
        <fog attach="fog" args={['#060a13', 14, 34]} />
        <ambientLight intensity={0.35} />
        <directionalLight position={[6, 10, 4]} intensity={1.4} />
        <pointLight position={[-6, 4, -4]} intensity={30} color="#22d3ee" />
        <pointLight position={[6, 2, -6]} intensity={20} color="#a78bfa" />
        {children}
        <OrbitControls target={target} enableDamping autoRotate={autoRotate} autoRotateSpeed={0.6} maxDistance={30} minDistance={2} />
        <EffectComposer>
          <Bloom intensity={0.9} luminanceThreshold={0.55} luminanceSmoothing={0.2} mipmapBlur />
          <Vignette eskil={false} offset={0.2} darkness={0.75} />
        </EffectComposer>
      </Canvas>
      </LabelLayerContext.Provider>
      <div ref={labels} className="stage-labels" />
      <span className="stage-hint">{t('trascina per ruotare · rotella per lo zoom')}</span>
    </div>
  );
}
