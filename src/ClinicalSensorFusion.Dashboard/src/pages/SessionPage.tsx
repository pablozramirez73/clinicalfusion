import { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { usePose, useSession, useSpectrogram } from '../api/hooks';
import { jointLabel, modeLabel, type SummaryRow } from '../api/types';
import { ErrorBox, Legend, Loading, Panel } from '../components/Feedback';
import { fmtDateTime } from '../components/format';
import { SkeletonReplay3D } from '../three/SkeletonReplay3D';
import { Spectrogram3D } from '../three/Spectrogram3D';
import { Stage } from '../three/Stage';

export default function SessionPage() {
  const { id = '' } = useParams();
  const session = useSession(id);
  const hasPose = (session.data?.streams.PoseLandmarks3D ?? 0) > 0;
  const hasMotion = (session.data?.streams.Motion ?? 0) > 0;
  const pose = usePose(id, hasPose);
  const spectrogram = useSpectrogram(id, hasMotion);

  if (session.isPending) return <Loading />;
  if (session.error) return <ErrorBox error={session.error} />;
  const s = session.data;

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <p className="breadcrumb"><Link to={`/patients/${s.patientId}`}>← {s.patientName ?? s.patientCode}</Link></p>
          <h1>Rilevazione del {fmtDateTime(s.startedAt)}</h1>
          <p className="muted">{modeLabel[s.mode]} · {s.operatorName} · {s.deviceModel ?? 'dispositivo sconosciuto'}</p>
        </div>
      </header>
      {s.notes && <div className="note">{s.notes}</div>}

      {hasPose && (
        <Panel title="Replay 3D dello scheletro" subtitle="Punti MediaPipe fusi con la profondità LiDAR, in metri · colore = flessione del ginocchio dx">
          {pose.isPending ? <Loading /> : pose.error ? <ErrorBox error={pose.error} /> : <PoseReplayPlayer replay={pose.data} />}
        </Panel>
      )}

      {hasMotion && (
        <Panel title="Spettrogramma 3D del tremore" subtitle="Accelerometro 100 Hz · potenza per frequenza nel tempo (somma dei tre assi)">
          {spectrogram.isPending ? <Loading /> : spectrogram.error ? <ErrorBox error={spectrogram.error} /> : (
            <Stage label="Superficie 3D dello spettrogramma del tremore" camera={[0, 5.5, 8.5]} height={460}>
              <Spectrogram3D data={spectrogram.data} />
            </Stage>
          )}
          <Legend items={[['4–6 Hz: tipico del tremore parkinsoniano a riposo', '#fb7185'], ['8–12 Hz: fisiologico potenziato / essenziale', '#fbbf24']]} />
        </Panel>
      )}

      <div className="grid-2">
        <Panel title="Indicatori">
          <Report rows={s.report} />
        </Panel>
        <div className="stack">
          <Panel title="Audio">
            {s.audio.length === 0 && <p className="muted">Nessuna registrazione.</p>}
            {s.audio.map((a) => (
              <div key={a.id} className="audio">
                <span className="muted">{a.durationSeconds.toFixed(1)} s · CAF 48 kHz Float32</span>
                {a.available
                  ? <audio controls preload="none" src={`/api/v1/sessions/${s.id}/audio/${a.id}.wav`} />
                  : <span className="chip chip--warn">file non ancora caricato</span>}
              </div>
            ))}
          </Panel>
          <Panel title="Analisi IA (Ollama)" subtitle="Testo di supporto, non sostituisce la valutazione clinica">
            {s.analyses.length === 0 && <p className="muted">Nessuna analisi.</p>}
            {s.analyses.map((a) => (
              <article key={a.id} className="analysis">
                <header className="muted">{fmtDateTime(a.createdAt)} · {a.model} · {a.language}</header>
                <p>{a.response}</p>
              </article>
            ))}
          </Panel>
          <Panel title="Flussi registrati">
            <ul className="streams">
              {Object.entries(s.streams).map(([kind, n]) => <li key={kind}><span>{kind}</span><strong>{n.toLocaleString('it-IT')}</strong></li>)}
            </ul>
          </Panel>
        </div>
      </div>
    </div>
  );
}

function PoseReplayPlayer({ replay }: { replay: import('../api/types').PoseReplay }) {
  const [frame, setFrame] = useState(0);
  const [playing, setPlaying] = useState(true);
  const fps = replay.duration > 0 ? replay.frameCount / replay.duration : 15;

  useEffect(() => {
    if (!playing || replay.frameCount < 2) return;
    const timer = window.setInterval(() => setFrame((f) => (f + 1) % replay.frameCount), 1000 / fps);
    return () => window.clearInterval(timer);
  }, [playing, fps, replay.frameCount]);

  const current = replay.frames[frame];
  const angles = useMemo(
    () => replay.jointNames.map((j, i) => [jointLabel[j], current?.angles[i]] as const).filter(([, v]) => v != null),
    [replay.jointNames, current],
  );

  return (
    <div className="replay">
      <Stage label="Scheletro 3D animato della rilevazione" camera={[1.3, 1.3, 2.1]} target={[0, 0.8, 0]} height={480}>
        <SkeletonReplay3D replay={replay} frame={frame} />
      </Stage>
      <div className="replay-side">
        <div className="timeline">
          <button onClick={() => setPlaying((p) => !p)} aria-label={playing ? 'Pausa' : 'Riproduci'}>{playing ? '❚❚' : '▶'}</button>
          <input type="range" min={0} max={replay.frameCount - 1} value={frame}
                 onChange={(e) => { setPlaying(false); setFrame(Number(e.target.value)); }} aria-label="Posizione nel replay" />
          <span className="muted">{(current?.t ?? 0).toFixed(1)} s</span>
        </div>
        <ul className="angles">
          {angles.map(([label, value]) => (
            <li key={label}>
              <span>{label}</span>
              <span className="angle-bar"><span style={{ width: `${Math.min(100, ((value ?? 0) / 180) * 100)}%` }} /></span>
              <strong>{Math.round(value ?? 0)}°</strong>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function Report({ rows }: { rows: SummaryRow[] }) {
  const sections = useMemo(() => {
    const map = new Map<string, SummaryRow[]>();
    for (const r of rows) map.set(r.section, [...(map.get(r.section) ?? []), r]);
    return [...map.entries()];
  }, [rows]);

  if (rows.length === 0) return <p className="muted">Riepilogo non disponibile.</p>;
  return (
    <div className="report">
      {sections.map(([section, items]) => (
        <div key={section}>
          <h3>{section}</h3>
          <dl>
            {items.map((r) => (
              <div key={r.label} className={r.warning ? 'warn' : undefined}>
                <dt>{r.label}</dt>
                <dd>{r.value}</dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  );
}
