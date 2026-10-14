import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useLogin } from '../api/hooks';
import { t } from '../i18n';
import { LanguageSelect } from '../i18n/LanguageSelect';

export function LoginPage() {
  const login = useLogin();
  const navigate = useNavigate();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');

  const submit = (e: FormEvent) => {
    e.preventDefault();
    login.mutate({ userName, password }, { onSuccess: () => navigate('/', { replace: true }) });
  };

  // Solo un 401 significa credenziali sbagliate: un errore del server non deve far credere di aver sbagliato password.
  const error = !login.error ? null
    : !(login.error instanceof ApiError) ? t('Server non raggiungibile.')
    : login.error.status === 401 ? t('Credenziali non valide.')
    : login.error.status === 429 ? t('Troppi tentativi: riprovare tra un minuto.')
    : t('Accesso non riuscito: errore del server ({0}).', login.error.status);

  return (
    <div className="login">
      <div className="login-glow" aria-hidden />
      <form className="login-card" onSubmit={submit}>
        <img src="/favicon.svg" alt="" width={56} height={56} />
        <h1>Clinical Sensor Fusion</h1>
        <p className="muted">{t('Area riservata al personale sanitario')}</p>
        <label>
          {t('Utente')}
          <input autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} required autoFocus />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        {error && <div className="alert" role="alert">{error}</div>}
        <LanguageSelect />
        <button className="primary" disabled={login.isPending}>{login.isPending ? t('Accesso…') : t('Accedi')}</button>
      </form>
    </div>
  );
}
