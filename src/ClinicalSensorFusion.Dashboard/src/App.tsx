import { useQueryClient } from '@tanstack/react-query';
import { Fragment, lazy, Suspense, useEffect } from 'react';
import { Navigate, Route, Routes, useNavigate } from 'react-router-dom';
import { setUnauthorizedHandler } from './api/client';
import { keys, useMe } from './api/hooks';
import { Layout } from './components/Layout';
import { Loading } from './components/Feedback';
import { LoginPage } from './pages/LoginPage';
import { useLanguage } from './i18n';

// Le pagine con grafici 3D sono caricate su richiesta: il login resta leggero.
const OverviewPage = lazy(() => import('./pages/OverviewPage'));
const PatientsPage = lazy(() => import('./pages/PatientsPage'));
const PatientPage = lazy(() => import('./pages/PatientPage'));
const SessionPage = lazy(() => import('./pages/SessionPage'));
const SystemPage = lazy(() => import('./pages/SystemPage'));

export function App() {
  const me = useMe();
  // Cambiando lingua l'albero si rimonta: i testi si traducono mentre si disegnano, senza hook in ogni componente.
  const language = useLanguage();
  const navigate = useNavigate();
  const qc = useQueryClient();

  useEffect(() => {
    setUnauthorizedHandler(() => {
      qc.setQueryData(keys.me, null);
      navigate('/login', { replace: true });
    });
  }, [navigate, qc]);

  if (me.isPending) return <Loading full />;
  if (!me.data) {
    return (
      <Routes key={language}>
        <Route path="*" element={<LoginPage />} />
      </Routes>
    );
  }

  return (
    <Fragment key={language}>
    <Layout user={me.data}>
      <Suspense fallback={<Loading />}>
        <Routes>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/patients" element={<PatientsPage />} />
          <Route path="/patients/new" element={<PatientPage />} />
          <Route path="/patients/:id" element={<PatientPage />} />
          <Route path="/sessions/:id" element={<SessionPage />} />
          <Route path="/system" element={<SystemPage />} />
          <Route path="/login" element={<Navigate to="/" replace />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </Suspense>
    </Layout>
    </Fragment>
  );
}
