import type { KeyboardEvent } from 'react';

/**
 * Una riga di tabella che apre una pagina: cliccabile, raggiungibile con Tab e attivabile con Invio o
 * Spazio, annunciata come collegamento dai lettori di schermo.
 */
export function rowLink(open: () => void) {
  return {
    className: 'clickable',
    role: 'link',
    tabIndex: 0,
    onClick: open,
    onKeyDown: (e: KeyboardEvent) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        open();
      }
    },
  };
}
