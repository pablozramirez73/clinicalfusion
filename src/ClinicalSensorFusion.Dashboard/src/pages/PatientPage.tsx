import { useMemo, useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiError } from '../api/client';
import { useDeletePatient, usePatient, usePatientSessions, useSavePatient, useTrends } from '../api/hooks';
import { allJoints, jointLabel, modeLabel, type PatientInput, type Sex, type TrendPoint } from '../api/types';
import { ErrorBox, Kpi, Loading, Panel } from '../components/Feedback';
import { age, fmtDateTime, fmtDuration, fmtShortDate } from '../components/format';
import { Bars3D, type Bar } from '../three/Bars3D';
import { modeColor, palette, turbo } from '../three/colormap';
import { Stage } from '../three/Stage';
import { t } from '../i18n';
import { rowLink } from '../components/rowLink';

const empty: PatientInput = {
  code: '', firstName: null, lastName: null, birthDate: null, sex: 'Unknown', fiscalCode: null,
  phone: null, email: null, address: null, diagnosis: null, notes: null, version: null,
};

export default function PatientPage() {
  const { id } = useParams();
  return id ? <ExistingPatient id={id} /> : <PatientForm initial={empty} />;
}

function ExistingPatient({ id }: { id: string }) {
  const patient = usePatient(id);
  const sessions = usePatientSessions(id);
  const trends = useTrends(id);
  const navigate = useNavigate();

  if (patient.isPending) return <Loading />;
  if (patient.error) return <ErrorBox error={patient.error} />;
  const p = patient.data;
  const latest = trends.data?.at(-1);
  const first = trends.data?.[0];
  const kneeGain = latest && first ? (latest.rom.RightKneeFlexion?.max ?? 0) - (first.rom.RightKneeFlexion?.max ?? 0) : null;

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>{p.lastName ? `${p.lastName} ${p.firstName ?? ''}` : p.code}</h1>
          <p className="muted">
            <code>{p.code}</code>
            {p.birthDate && ` · ${t('{0} anni', age(p.birthDate) ?? 0)}`}
            {p.diagnosis && ` · ${p.diagnosis}`}
          </p>
        </div>
        {/* Esportazione HL7 FHIR R4: anagrafica, rilevazioni e osservazioni in un unico Bundle JSON. */}
        <a className="button" href={`/fhir/Patient/${p.id}/$everything`} download={`paziente-${p.code}.fhir.json`}
           title={t('Scarica i dati del paziente in formato HL7 FHIR R4 (Bundle JSON)')}>
          {t('Esporta FHIR')}
        </a>
      </header>

      <div className="kpis">
        <Kpi label={t('Rilevazioni')} value={p.sessionCount} accent={palette.cyan} />
        <Kpi label={t('Flessione max ginocchio dx')} value={latest?.rom.RightKneeFlexion ? `${latest.rom.RightKneeFlexion.max.toFixed(0)}°` : '—'}
             hint={kneeGain != null && trends.data!.length > 1 ? t('{0}° dalla prima', `${kneeGain >= 0 ? '+' : ''}${kneeGain.toFixed(0)}`) : undefined} accent={palette.teal} />
        <Kpi label={t('Tremore (ultima)')} value={latest?.tremorFrequencyHz ? `${latest.tremorFrequencyHz.toFixed(1)} Hz` : '—'}
             hint={latest?.tremorRmsMs2 ? `${latest.tremorRmsMs2.toFixed(3)} m/s²` : undefined} accent={palette.rose} />
        <Kpi label={t('Cadenza (ultima)')} value={latest?.cadenceStepsPerMinute ? `${latest.cadenceStepsPerMinute.toFixed(0)}/min` : '—'} accent={palette.amber} />
      </div>

      {trends.error && <ErrorBox error={trends.error} />}
      {trends.data && trends.data.length > 0 && <RomLandscape trends={trends.data} onSelect={(sid) => navigate(`/sessions/${sid}`)} />}

      <div className="grid-2">
        <Panel title={t('Rilevazioni')}>
          {sessions.isPending ? <Loading /> : sessions.error ? <ErrorBox error={sessions.error} /> : (
            <table className="table">
              <thead><tr><th>{t('Data')}</th><th>{t('Modalità')}</th><th>{t('Durata')}</th><th>{t('Operatore')}</th></tr></thead>
              <tbody>
                {sessions.data?.map((s) => (
                  <tr key={s.id} {...rowLink(() => navigate(`/sessions/${s.id}`))}>
                    <td>{fmtDateTime(s.startedAt)}</td>
                    <td><span className="chip" style={{ '--chip': modeColor[s.mode] } as React.CSSProperties}>{t(modeLabel[s.mode])}</span></td>
                    <td>{fmtDuration(s.durationSeconds)}</td>
                    <td>{s.operatorName}</td>
                  </tr>
                ))}
                {sessions.data?.length === 0 && <tr><td colSpan={4} className="muted">{t('Nessuna rilevazione.')}</td></tr>}
              </tbody>
            </table>
          )}
        </Panel>
        <PatientForm key={`${p.id}:${p.version}`} initial={p} id={p.id} />
      </div>
    </div>
  );
}

/**
 * "Paesaggio" dell'escursione articolare: righe = articolazioni, colonne = rilevazioni in ordine di data,
 * altezza/colore = ROM. Mostra a colpo d'occhio progressi della riabilitazione e asimmetrie destra/sinistra.
 */
function RomLandscape({ trends, onSelect }: { trends: TrendPoint[]; onSelect: (id: string) => void }) {
  const { bars, joints } = useMemo(() => {
    const joints = allJoints.filter((j) => trends.some((point) => point.rom[j]));
    const max = Math.max(1, ...trends.flatMap((point) => joints.map((j) => (point.rom[j] ? point.rom[j]!.max - point.rom[j]!.min : 0))));
    const bars: Bar[] = [];
    trends.forEach((point, x) => {
      joints.forEach((j, z) => {
        const r = point.rom[j];
        if (!r) return;
        const rom = r.max - r.min;
        const [cr, cg, cb] = turbo(rom / max);
        bars.push({
          key: `${point.sessionId}-${j}`, x, z, value: rom,
          color: `rgb(${(cr * 255) | 0},${(cg * 255) | 0},${(cb * 255) | 0})`,
          tooltip: `${fmtShortDate(point.startedAt)} · ${t(jointLabel[j])}: ROM ${rom.toFixed(0)}° (${r.min.toFixed(0)}°–${r.max.toFixed(0)}°)`,
        });
      });
    });
    return { bars, joints };
  }, [trends]);

  return (
    <Panel title={t('Paesaggio dell\'escursione articolare')} subtitle={t('Ogni colonna è una rilevazione, ogni riga un\'articolazione · altezza e colore = ROM')}>
      <Stage label={t('Grafico 3D dell\'escursione articolare per rilevazione')} camera={[-3, 7, 11]} height={480}>
        <Bars3D bars={bars} xLabels={trends.map((point) => fmtShortDate(point.startedAt))} zLabels={joints.map((j) => t(jointLabel[j]))} spacing={1.1} rowSpacing={1.0} />
      </Stage>
      <div className="session-strip">
        {trends.map((point) => (
          <button key={point.sessionId} className="link" onClick={() => onSelect(point.sessionId)}>{fmtShortDate(point.startedAt)}</button>
        ))}
      </div>
    </Panel>
  );
}

function PatientForm({ initial, id }: { initial: PatientInput; id?: string }) {
  const [form, setForm] = useState<PatientInput>(initial);
  const save = useSavePatient();
  const remove = useDeletePatient();
  const navigate = useNavigate();

  const set = <K extends keyof PatientInput>(key: K) => (e: { target: { value: string } }) =>
    setForm((f) => ({ ...f, [key]: e.target.value === '' ? null : e.target.value }));

  const submit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate({ id, input: form }, {
      onSuccess: (p) => {
        setForm({ ...p, version: p.version });
        if (!id) navigate(`/patients/${p.id}`, { replace: true });
      },
    });
  };

  const fieldErrors = save.error instanceof ApiError ? save.error.details ?? {} : {};
  const err = (k: string) => fieldErrors[k]?.join(' ');

  return (
    <Panel title={id ? t('Anagrafica') : t('Nuovo paziente')} subtitle={t('Dati personali: accesso tracciato nel registro di audit')}>
      <form className="form" onSubmit={submit}>
        <Field label={t('Codice (come nell\'app)')} error={err('code')}>
          <input value={form.code} onChange={set('code')} required maxLength={64} />
        </Field>
        <div className="form-row">
          <Field label={t('Cognome')} error={err('name')}><input value={form.lastName ?? ''} onChange={set('lastName')} maxLength={100} /></Field>
          <Field label={t('Nome')}><input value={form.firstName ?? ''} onChange={set('firstName')} maxLength={100} /></Field>
        </div>
        <div className="form-row">
          <Field label={t('Data di nascita')} error={err('birthDate')}><input type="date" value={form.birthDate ?? ''} onChange={set('birthDate')} /></Field>
          <Field label={t('Sesso')}>
            <select value={form.sex} onChange={(e) => setForm((f) => ({ ...f, sex: e.target.value as Sex }))}>
              <option value="Unknown">{t('Non indicato')}</option>
              <option value="Female">{t('Femmina')}</option>
              <option value="Male">{t('Maschio')}</option>
              <option value="Other">{t('Altro')}</option>
            </select>
          </Field>
        </div>
        <Field label={t('Codice fiscale')} error={err('fiscalCode')}>
          <input value={form.fiscalCode ?? ''} onChange={set('fiscalCode')} maxLength={16} style={{ textTransform: 'uppercase' }} />
        </Field>
        <div className="form-row">
          <Field label={t('Telefono')}><input type="tel" value={form.phone ?? ''} onChange={set('phone')} /></Field>
          <Field label="Email" error={err('email')}><input type="email" value={form.email ?? ''} onChange={set('email')} /></Field>
        </div>
        <Field label={t('Indirizzo')}><input value={form.address ?? ''} onChange={set('address')} /></Field>
        <Field label={t('Diagnosi / motivo della presa in carico')}><textarea rows={2} value={form.diagnosis ?? ''} onChange={set('diagnosis')} /></Field>
        <Field label={t('Note')}><textarea rows={3} value={form.notes ?? ''} onChange={set('notes')} /></Field>

        {save.error && !Object.keys(fieldErrors).length && <ErrorBox error={save.error} />}
        {remove.error && <ErrorBox error={remove.error} />}
        {save.isSuccess && <div className="success" role="status">{t('Salvato.')}</div>}
        <div className="form-actions">
          <button className="primary" disabled={save.isPending}>{save.isPending ? t('Salvataggio…') : t('Salva')}</button>
          {id && (
            <button
              type="button"
              className="danger"
              disabled={remove.isPending}
              onClick={() => {
                if (window.confirm(t('Eliminare definitivamente il paziente con tutte le rilevazioni e gli audio? L\'operazione non è reversibile.'))) {
                  remove.mutate(id, { onSuccess: () => navigate('/patients', { replace: true }) });
                }
              }}
            >
              {t('Elimina')}
            </button>
          )}
        </div>
      </form>
    </Panel>
  );
}

function Field({ label, error, children }: { label: string; error?: string; children: React.ReactNode }) {
  return (
    <label className={`field ${error ? 'field--error' : ''}`}>
      <span>{label}</span>
      {children}
      {error && <small>{error}</small>}
    </label>
  );
}
