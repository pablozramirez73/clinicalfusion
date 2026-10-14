import { Component, type ErrorInfo, type ReactNode } from 'react';
import { t } from '../i18n';

/** Un errore di disegno in una pagina mostra un messaggio invece di lasciare lo schermo vuoto. */
export class ErrorBoundary extends Component<{ children: ReactNode }, { error: Error | null }> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(error, info.componentStack);
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <div className="alert" role="alert">
        <p>{t('Questa pagina non si è potuta mostrare.')} {this.state.error.message}</p>
        <button onClick={() => window.location.reload()}>{t('Ricarica')}</button>
      </div>
    );
  }
}
