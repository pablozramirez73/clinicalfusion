import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, del, post, put } from './client';
import type {
  CurrentUser, Overview, PagedResult, PatientDetail, PatientInput, PatientListItem, PoseReplay,
  SessionDetail, SessionListItem, Spectrogram, SystemHealth, SystemMetrics, TrendPoint,
} from './types';

export const keys = {
  me: ['me'] as const,
  overview: ['overview'] as const,
  patients: (search: string, page: number, placeholders: boolean) => ['patients', search, page, placeholders] as const,
  patient: (id: string) => ['patient', id] as const,
  patientSessions: (id: string) => ['patient', id, 'sessions'] as const,
  trends: (id: string) => ['patient', id, 'trends'] as const,
  session: (id: string) => ['session', id] as const,
  pose: (id: string) => ['session', id, 'pose'] as const,
  spectrogram: (id: string) => ['session', id, 'spectrogram'] as const,
  systemHealth: ['system', 'health'] as const,
  systemMetrics: ['system', 'metrics'] as const,
};

export const useMe = () => useQuery({ queryKey: keys.me, queryFn: () => api<CurrentUser>('auth/me'), retry: false, staleTime: Infinity });

export function useLogin() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (v: { userName: string; password: string }) => post<CurrentUser>('auth/login', v),
    onSuccess: (user) => qc.setQueryData(keys.me, user),
  });
}

export function useLogout() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: () => post<void>('auth/logout'), onSettled: () => qc.clear() });
}

export const useOverview = () => useQuery({ queryKey: keys.overview, queryFn: () => api<Overview>('dashboard/overview'), refetchInterval: 60_000 });

export const usePatients = (search: string, page: number, placeholders: boolean) =>
  useQuery({
    queryKey: keys.patients(search, page, placeholders),
    queryFn: () => api<PagedResult<PatientListItem>>(
      `patients?search=${encodeURIComponent(search)}&page=${page}&pageSize=20${placeholders ? '&placeholdersOnly=true' : ''}`),
    placeholderData: keepPreviousData,
  });

export const usePatient = (id: string) => useQuery({ queryKey: keys.patient(id), queryFn: () => api<PatientDetail>(`patients/${id}`) });
export const usePatientSessions = (id: string) => useQuery({ queryKey: keys.patientSessions(id), queryFn: () => api<SessionListItem[]>(`patients/${id}/sessions`) });
export const useTrends = (id: string) => useQuery({ queryKey: keys.trends(id), queryFn: () => api<TrendPoint[]>(`patients/${id}/trends`) });

export function useSavePatient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id?: string; input: PatientInput }) =>
      id ? put<PatientDetail>(`patients/${id}`, input) : post<PatientDetail>('patients', input),
    onSuccess: (p) => {
      qc.setQueryData(keys.patient(p.id), p);
      void qc.invalidateQueries({ queryKey: ['patients'] });
      void qc.invalidateQueries({ queryKey: keys.overview });
    },
  });
}

export function useDeletePatient() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => del(`patients/${id}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['patients'] });
      void qc.invalidateQueries({ queryKey: keys.overview });
    },
  });
}

export const useSession = (id: string) => useQuery({ queryKey: keys.session(id), queryFn: () => api<SessionDetail>(`sessions/${id}`) });
export const usePose = (id: string, enabled: boolean) =>
  useQuery({ queryKey: keys.pose(id), queryFn: () => api<PoseReplay>(`sessions/${id}/pose?maxFrames=900`), enabled, staleTime: Infinity });
export const useSpectrogram = (id: string, enabled: boolean) =>
  useQuery({ queryKey: keys.spectrogram(id), queryFn: () => api<Spectrogram>(`sessions/${id}/spectrogram?window=256&columns=160&maxHz=20`), enabled, staleTime: Infinity });

// Stato del sistema: aggiornato da solo, la pagina serve a guardare cosa succede adesso.
export const useSystemHealth = () => useQuery({ queryKey: keys.systemHealth, queryFn: () => api<SystemHealth>('system/health'), refetchInterval: 15_000 });
export const useSystemMetrics = () => useQuery({ queryKey: keys.systemMetrics, queryFn: () => api<SystemMetrics>('system/metrics'), refetchInterval: 15_000 });
