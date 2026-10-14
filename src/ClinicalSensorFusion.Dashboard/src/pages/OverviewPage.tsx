import { useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useOverview } from '../api/hooks';
import { jointLabel, modeLabel, type CaptureMode } from '../api/types';
import { ErrorBox, Kpi, Legend, Loading, Panel } from '../components/Feedback';
import { fmtDateTime, fmtDuration, fmtShortDate } from '../components/format';
import { Bars3D, type Bar } from '../three/Bars3D';
import { modeColor, palette, turbo } from '../three/colormap';
import { Stage } from '../three/Stage';
import { TremorGalaxy3D } from '../three/TremorGalaxy3D';

const MODES: CaptureMode[] = ['BodyLidar', 'HandsLidar', 'FaceTrueDepth'];

export default function OverviewPage() {
  const { data, error, isPending } = useOverview();
  const navigate = useNavigate();

  const activity = useMemo(() => {
    if (!data) return null;
    const weeks = [...new Set(data.weekly.map((w) => w.weekStart))].sort();
    const bars: Bar[] = data.weekly.map((w) => ({
      key: `${w.weekStart}-${w.mode}`,
      x: weeks.indexOf(w.weekStart),
      z: MODES.indexOf(w.mode),
      value: w.sessions,
      color: modeColor[w.mode],
      tooltip: `${fmtShortDate(w.weekStart)} · ${modeLabel[w.mode]}: ${w.sessions}`,
    }));
    return { bars, weeks: weeks.map((w, i) => (i % 2 === 0 ? fmtShortDate(w) : '')) };
  }, [data]);

  const rom = useMemo(() => {
    if (!data) return null;
    const max = Math.max(1, ...data.jointAverages.map((j) => j.meanRom));
    const bars: Bar[] = data.jointAverages.map((j, i) => {
      const [r, g, b] = turbo(j.meanRom / max);
      return {
        key: j.joint, x: i, z: 0, value: j.meanRom,
        color: `rgb(${(r * 255) | 0},${(g * 255) | 0},${(b * 255) | 0})`,
        tooltip: `${j.name}: ${j.meanRom.toFixed(0)}° (${j.sessions} rilevazioni)`,
      };
    });
    return { bars, labels: data.jointAverages.map((j) => jointLabel[j.joint]) };
  }, [data]);

  if (isPending) return <Loading />;
  if (error) return <ErrorBox error={error} />;

  return (
    <div className="page">
      <header className="page-header">
        <div>
          <h1>Panoramica</h1>
          <p className="muted">Ultime 12 settimane · aggiornamento automatico</p>
        </div>
      </header>

      <div className="kpis">
        <Kpi label="Pazienti" value={data.patients} hint={data.placeholderPatients ? `${data.placeholderPatients} da completare` : 'anagrafiche complete'} accent={palette.cyan} />
        <Kpi label="Rilevazioni" value={data.sessions} hint={`${data.sessionsLast30Days} negli ultimi 30 giorni`} accent={palette.violet} />
        <Kpi label="Ore registrate" value={data.recordedHours.toFixed(1)} hint="tutti i sensori" accent={palette.teal} />
        <Kpi label="Registrazioni audio" value={data.audioFiles} hint="CAF 48 kHz" accent={palette.amber} />
      </div>

      <div className="grid-2">
        <Panel title="Attività settimanale" subtitle="Rilevazioni per settimana e modalità di acquisizione">
          {activity && activity.bars.length > 0 ? (
            <Stage label="Istogramma 3D delle rilevazioni settimanali" camera={[3, 6, 10]}>
              <Bars3D bars={activity.bars} xLabels={activity.weeks} zLabels={MODES.map((m) => modeLabel[m])} rowSpacing={1.4} />
            </Stage>
          ) : <p className="muted">Nessuna rilevazione nel periodo.</p>}
          <Legend items={MODES.map((m) => [modeLabel[m], modeColor[m]])} />
        </Panel>

        <Panel title="Galassia dei tremori" subtitle="Frequenza × ampiezza × tempo · clic su una sfera per aprire la rilevazione">
          {data.tremor.length > 0 ? (
            <Stage label="Grafico 3D a dispersione dei tremori" camera={[6, 4, 7]} target={[0, 1, 0]}>
              <TremorGalaxy3D points={data.tremor} onSelect={(id) => navigate(`/sessions/${id}`)} />
            </Stage>
          ) : <p className="muted">Nessun tremore misurato.</p>}
          <Legend items={[['4–6 Hz', palette.rose], ['6–8 Hz', palette.violet], ['8–12 Hz', palette.amber], ['altro', palette.cyan]]} />
        </Panel>
      </div>

      <Panel title="Escursione articolare media" subtitle="ROM medio (max − min) per articolazione, tutte le rilevazioni del periodo">
        {rom && rom.bars.length > 0 ? (
          <Stage label="Barre 3D dell'escursione articolare media" camera={[0, 4.5, 10]} height={340}>
            <Bars3D bars={rom.bars} xLabels={rom.labels} zLabels={['']} spacing={1.35} />
          </Stage>
        ) : <p className="muted">Nessun dato articolare.</p>}
      </Panel>

      <Panel title="Rilevazioni recenti">
        <table className="table">
          <thead>
            <tr><th>Data</th><th>Paziente</th><th>Modalità</th><th>Durata</th><th>Tremore</th><th>Operatore</th></tr>
          </thead>
          <tbody>
            {data.recent.map((s) => (
              <tr key={s.id} onClick={() => navigate(`/sessions/${s.id}`)} className="clickable">
                <td>{fmtDateTime(s.startedAt)}</td>
                <td><Link to={`/patients/${s.patientId}`} onClick={(e) => e.stopPropagation()}>{s.patientName ?? s.patientCode}</Link></td>
                <td><span className="chip" style={{ '--chip': modeColor[s.mode] } as React.CSSProperties}>{modeLabel[s.mode]}</span></td>
                <td>{fmtDuration(s.durationSeconds)}</td>
                <td>{s.tremorFrequencyHz ? `${s.tremorFrequencyHz.toFixed(1)} Hz` : '—'}</td>
                <td>{s.operatorName}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Panel>
    </div>
  );
}
