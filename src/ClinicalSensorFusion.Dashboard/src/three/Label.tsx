import { Html } from '@react-three/drei';
import { createContext, useContext, type ComponentProps, type RefObject } from 'react';

/** Contenitore DOM delle etichette, fornito da <Stage> (fratello del <Canvas>, non suo figlio). */
export const LabelLayerContext = createContext<RefObject<HTMLDivElement | null> | null>(null);

/**
 * Etichetta HTML ancorata a un punto 3D (drei <Html>), montata nel livello dedicato di <Stage>.
 *
 * Perché: di default <Html> inserisce i suoi nodi nel <div> che React Three Fiber crea attorno al canvas.
 * Quando si cambia pagina React DOM smonta prima quel <div> e solo dopo R3F smonta la scena: la pulizia di
 * <Html> prova a rimuovere nodi già staccati → "NotFoundError: removeChild". Un livello gestito da <Stage>
 * resta valido finché tutte le etichette sono state smontate.
 */
export function Label(props: ComponentProps<typeof Html>) {
  const layer = useContext(LabelLayerContext);
  return <Html {...props} portal={layer as RefObject<HTMLElement> | undefined ?? undefined} />;
}
