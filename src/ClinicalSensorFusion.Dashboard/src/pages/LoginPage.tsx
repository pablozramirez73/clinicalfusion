import { useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useAuthProviders, useLogin, useRegister } from '../api/hooks';
import { t } from '../i18n';
import { LanguageSelect } from '../i18n/LanguageSelect';

export function LoginPage() {
  const login = useLogin();
  const providers = useAuthProviders();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const register = useRegister();
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [confirm, setConfirm] = useState('');
  const [registered, setRegistered] = useState(false);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    login.mutate({ userName, password }, { onSuccess: () => navigate('/', { replace: true }) });
  };

  const submitRegistration = (e: FormEvent) => {
    e.preventDefault();
    if (password !== confirm) return;
    register.mutate({ email: userName, displayName, password }, {
      onSuccess: () => {
        setRegistered(true);
        setMode('login');
        setPassword('');
        setConfirm('');
      },
    });
  };

  const error = login.error instanceof ApiError
    ? login.error.status === 429 ? t('Troppi tentativi: riprovare tra un minuto.')
      : login.error.status === 403 ? t('La tua richiesta di accesso è ancora in attesa di approvazione.')
      : t('Credenziali non valide.')
    : login.error ? t('Server non raggiungibile.') : null;

  const registerError = password !== confirm && confirm.length > 0
    ? t('Le due password non coincidono.')
    : register.error instanceof ApiError
      ? register.error.status === 429 ? t('Troppi tentativi: riprovare tra un minuto.') : register.error.message
      : register.error ? t('Server non raggiungibile.') : null;

  // Ritorno da Google: annullato, oppure account non autorizzato per questa dashboard.
  const googleError = params.get('error') === 'google-denied'
    ? t('Accesso con questo account Google non consentito.')
    : params.get('error') === 'google' ? t('Accesso con Google non riuscito o annullato.') : null;

  // Registrazione: la richiesta resta in attesa finché un amministratore non la approva.
  const notice = registered
    ? t('Richiesta di accesso inviata. Potrai entrare appena un amministratore la approva.')
    : params.get('status') === 'requested'
      ? t('Richiesta di accesso inviata. Potrai entrare con Google appena un amministratore la approva.')
      : params.get('status') === 'pending' ? t('La tua richiesta di accesso è ancora in attesa di approvazione.') : null;

  if (mode === 'register') {
    return (
      <div className="login">
        <div className="login-glow" aria-hidden />
        <form className="login-card" onSubmit={submitRegistration}>
          <img src="/favicon.svg" alt="" width={56} height={56} />
          <h1>{t('Crea un nuovo account')}</h1>
          <p className="muted">{t('La richiesta va approvata da un amministratore prima di poter entrare.')}</p>
          <label>
            {t('Nome e cognome')}
            <input autoComplete="name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required autoFocus />
          </label>
          <label>
            Email
            <input type="email" autoComplete="email" value={userName} onChange={(e) => setUserName(e.target.value)} required />
          </label>
          <label>
            Password
            <input type="password" autoComplete="new-password" minLength={12} value={password} onChange={(e) => setPassword(e.target.value)} required />
          </label>
          <label>
            {t('Ripeti la password')}
            <input type="password" autoComplete="new-password" minLength={12} value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
          </label>
          <p className="muted login-hint">{t('Almeno 12 caratteri.')}</p>
          {registerError && <div className="alert" role="alert">{registerError}</div>}
          <button className="primary" disabled={register.isPending || password !== confirm}>
            {register.isPending ? t('Invio…') : t('Invia la richiesta')}
          </button>
          {providers.data?.google && (
            <a className="google-button" href="/api/v1/auth/google?returnUrl=%2F">{t('Registrati con Google')}</a>
          )}
          <button type="button" className="link" onClick={() => setMode('login')}>{t('Hai già un account? Accedi')}</button>
        </form>
      </div>
    );
  }

  return (
    <div className="login">
      <div className="login-glow" aria-hidden />
      <form className="login-card" onSubmit={submit}>
        <img src="/favicon.svg" alt="" width={56} height={56} />
        <h1>Clinical Sensor Fusion</h1>
        <p className="muted">{t('Area riservata al personale sanitario')}</p>
        <label>
          {t('Utente o email')}
          <input autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} required autoFocus />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        {(error ?? googleError) && <div className="alert" role="alert">{error ?? googleError}</div>}
        {notice && !error && <div className="notice" role="status">{notice}</div>}
        <LanguageSelect />
        <button className="primary" disabled={login.isPending}>{login.isPending ? t('Accesso…') : t('Accedi')}</button>
        {providers.data?.google && (
          <>
            <div className="login-divider"><span>{t('oppure')}</span></div>
            {/* Navigazione vera, non fetch: si va su Google e si torna qui con la sessione aperta. */}
            <a className="google-button" href="/api/v1/auth/google?returnUrl=%2F">
              <svg width="18" height="18" viewBox="0 0 48 48" aria-hidden>
                <path fill="#FFC107" d="M43.6 20.5H42V20H24v8h11.3C33.7 32.7 29.2 36 24 36c-6.6 0-12-5.4-12-12s5.4-12 12-12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 12.9 4 4 12.9 4 24s8.9 20 20 20 20-8.9 20-20c0-1.3-.1-2.4-.4-3.5z" />
                <path fill="#FF3D00" d="m6.3 14.7 6.6 4.8C14.7 15.1 19 12 24 12c3.1 0 5.8 1.2 7.9 3.1l5.7-5.7C34 6.1 29.3 4 24 4 16.3 4 9.7 8.3 6.3 14.7z" />
                <path fill="#4CAF50" d="M24 44c5.2 0 9.9-2 13.4-5.2l-6.2-5.2C29.2 35.1 26.7 36 24 36c-5.2 0-9.6-3.3-11.3-8l-6.5 5C9.5 39.6 16.2 44 24 44z" />
                <path fill="#1976D2" d="M43.6 20.5H42V20H24v8h11.3c-.8 2.2-2.2 4.2-4.1 5.6l6.2 5.2C36.9 39.2 44 34 44 24c0-1.3-.1-2.4-.4-3.5z" />
              </svg>
              {t('Accedi o registrati con Google')}
            </a>
            <p className="muted login-hint">{t('Al primo accesso con Google si crea una richiesta: entri dopo l\'approvazione di un amministratore.')}</p>
          </>
        )}
        <button type="button" className="link" onClick={() => { setMode('register'); setRegistered(false); }}>
          {t('Non hai un account? Crea un nuovo account')}
        </button>
      </form>
    </div>
  );
}
