import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useLogin } from '../api/hooks';

export function LoginPage() {
  const login = useLogin();
  const navigate = useNavigate();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');

  const submit = (e: FormEvent) => {
    e.preventDefault();
    login.mutate({ userName, password }, { onSuccess: () => navigate('/', { replace: true }) });
  };

  const error = login.error instanceof ApiError
    ? login.error.status === 429 ? 'Troppi tentativi: riprovare tra un minuto.' : 'Credenziali non valide.'
    : login.error ? 'Server non raggiungibile.' : null;

  return (
    <div className="login">
      <div className="login-glow" aria-hidden />
      <form className="login-card" onSubmit={submit}>
        <img src="/favicon.svg" alt="" width={56} height={56} />
        <h1>Clinical Sensor Fusion</h1>
        <p className="muted">Area riservata al personale sanitario</p>
        <label>
          Utente
          <input autoComplete="username" value={userName} onChange={(e) => setUserName(e.target.value)} required autoFocus />
        </label>
        <label>
          Password
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        {error && <div className="alert" role="alert">{error}</div>}
        <button className="primary" disabled={login.isPending}>{login.isPending ? 'Accesso…' : 'Accedi'}</button>
      </form>
    </div>
  );
}
