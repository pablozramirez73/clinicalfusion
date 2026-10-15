"""Caratteristiche del movimento calcolate allo stesso modo da KINECAL (Kinect) e dalle rilevazioni dell'app (MediaPipe).

Solo misure interne al corpo. Su Android i punti 3D sono i *world landmarks* di MediaPipe, con l'origine al
centro delle anche: lo spostamento del corpo nella stanza non c'è, quindi niente velocità di cammino né
traiettoria del bacino nello spazio. Restano angoli articolari, altezza del bacino rispetto alle caviglie,
oscillazione del bacino sopra la base d'appoggio e ritmo dei passi (distanza tra le caviglie).

Tutto è riportato a 30 Hz e filtrato a 6 Hz (Butterworth del 4° ordine, senza ritardo), come fa KINECAL con
il Kinect (8 Hz): così il rumore diverso dei due sensori pesa meno sulle derivate.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
from scipy.signal import butter, filtfilt, find_peaks

FS = 30.0

# Punti usati, con il nome comune ai due scheletri. Sinistra e destra sono quelle del soggetto in entrambi.
JOINTS = [
    "l_shoulder", "r_shoulder", "l_elbow", "r_elbow", "l_wrist", "r_wrist",
    "l_hip", "r_hip", "l_knee", "r_knee", "l_ankle", "r_ankle", "l_foot", "r_foot",
]

KINECT = {
    "l_shoulder": "ShoulderLeft", "r_shoulder": "ShoulderRight", "l_elbow": "ElbowLeft", "r_elbow": "ElbowRight",
    "l_wrist": "WristLeft", "r_wrist": "WristRight", "l_hip": "HipLeft", "r_hip": "HipRight",
    "l_knee": "KneeLeft", "r_knee": "KneeRight", "l_ankle": "AnkleLeft", "r_ankle": "AnkleRight",
    "l_foot": "FootLeft", "r_foot": "FootRight",
}

# Indici di MediaPipe Pose (gli stessi di PoseLandmark.cs).
MEDIAPIPE = {
    "l_shoulder": 11, "r_shoulder": 12, "l_elbow": 13, "r_elbow": 14, "l_wrist": 15, "r_wrist": 16,
    "l_hip": 23, "r_hip": 24, "l_knee": 25, "r_knee": 26, "l_ankle": 27, "r_ankle": 28,
    "l_foot": 31, "r_foot": 32,
}

MOVEMENTS = ("tug", "sts5", "tandem", "semi_tandem", "walk3m")


@dataclass
class Skeleton:
    """Sequenza di pose: tempi (s) e punti [T, len(JOINTS), 3] in metri; NaN per i punti mancanti."""

    t: np.ndarray
    p: np.ndarray

    def __getitem__(self, joint: str) -> np.ndarray:
        return self.p[:, JOINTS.index(joint)]

    def mid(self, a: str, b: str) -> np.ndarray:
        return (self[a] + self[b]) / 2


def from_kinect(t: np.ndarray, points: np.ndarray, names: list[str]) -> Skeleton:
    idx = [names.index(KINECT[j]) for j in JOINTS]
    return Skeleton(np.asarray(t, float), np.asarray(points, float)[:, idx])


def from_mediapipe(t: np.ndarray, points33: np.ndarray) -> Skeleton:
    """``points33``: [T, 33, 3] oppure [T, 99] come nello stream PoseLandmarks3D dell'app."""
    p = np.asarray(points33, float).reshape(len(t), 33, 3)
    return Skeleton(np.asarray(t, float), p[:, [MEDIAPIPE[j] for j in JOINTS]])


def prepare(s: Skeleton, cutoff_hz: float = 6.0) -> Skeleton | None:
    """Ricampiona a 30 Hz, riempie i buchi brevi per interpolazione e filtra. None se i dati non bastano."""
    t = s.t - s.t[0]
    ok = np.isfinite(s.p).all(axis=2).all(axis=1)
    if ok.sum() < FS or t[-1] < 1.0:
        return None
    grid = np.arange(0, t[-1], 1 / FS)
    p = np.empty((len(grid), *s.p.shape[1:]))
    for j in range(s.p.shape[1]):
        for k in range(3):
            p[:, j, k] = np.interp(grid, t[ok], s.p[ok, j, k])
    b, a = butter(4, cutoff_hz / (FS / 2))
    if len(grid) > 3 * max(len(a), len(b)):
        p = filtfilt(b, a, p, axis=0)
    return Skeleton(grid, p)


def _unit(v: np.ndarray) -> np.ndarray:
    return v / np.linalg.norm(v, axis=-1, keepdims=True)


def up_axis(s: Skeleton) -> np.ndarray:
    """Verticale stimata dai dati: in media le spalle stanno sopra le caviglie, seduti o in piedi."""
    v = s.mid("l_shoulder", "r_shoulder") - s.mid("l_ankle", "r_ankle")
    return _unit(np.nanmean(_unit(v), axis=0))


def angle(a: np.ndarray, vertex: np.ndarray, c: np.ndarray) -> np.ndarray:
    u, v = _unit(a - vertex), _unit(c - vertex)
    return np.degrees(np.arccos(np.clip((u * v).sum(-1), -1, 1)))


def flexion(s: Skeleton, side: str, joint: str) -> np.ndarray:
    """Flessione come in JointAngleCalculator.cs: 180° − angolo interno (0° = segmento esteso)."""
    chain = {"hip": ("shoulder", "hip", "knee"), "knee": ("hip", "knee", "ankle")}[joint]
    a, v, c = (s[f"{side}_{x}"] for x in chain)
    return 180 - angle(a, v, c)


def leg_length(s: Skeleton) -> float:
    l = np.linalg.norm(s["l_hip"] - s["l_knee"], axis=1) + np.linalg.norm(s["l_knee"] - s["l_ankle"], axis=1)
    r = np.linalg.norm(s["r_hip"] - s["r_knee"], axis=1) + np.linalg.norm(s["r_knee"] - s["r_ankle"], axis=1)
    return float(np.nanmedian((l + r) / 2))


def pelvis_height(s: Skeleton) -> np.ndarray:
    """Altezza del bacino sopra le caviglie, in lunghezze di gamba (≈1 in piedi, ≈0.5 seduti)."""
    h = (s.mid("l_hip", "r_hip") - s.mid("l_ankle", "r_ankle")) @ up_axis(s)
    return h / leg_length(s)


def trunk_inclination(s: Skeleton) -> np.ndarray:
    """Inclinazione del tronco rispetto alla verticale, in gradi (0 = eretto)."""
    trunk = _unit(s.mid("l_shoulder", "r_shoulder") - s.mid("l_hip", "r_hip"))
    return np.degrees(np.arccos(np.clip(trunk @ up_axis(s), -1, 1)))


def _transitions(h: np.ndarray) -> tuple[list[tuple[int, int]], list[tuple[int, int]], float, float]:
    """Alzate e sedute: intervalli in cui l'altezza del bacino attraversa il 10–90% tra seduto e in piedi."""
    lo, hi = np.percentile(h, 5), np.percentile(h, 95)
    if hi - lo < 0.15:  # nessuna alzata riconoscibile: il soggetto resta in piedi o seduto
        return [], [], lo, hi
    low, high = lo + 0.1 * (hi - lo), lo + 0.9 * (hi - lo)
    state = "low" if h[0] < (lo + hi) / 2 else "high"
    rises, sits = [], []
    start = None
    for i, x in enumerate(h):
        if state == "low":
            if x > low and start is None:
                start = i
            if x < low:
                start = None
            if x > high:
                rises.append((start if start is not None else i, i))
                state, start = "high", None
        else:
            if x < high and start is None:
                start = i
            if x > high:
                start = None
            if x < low:
                sits.append((start if start is not None else i, i))
                state, start = "low", None
    return rises, sits, lo, hi


def _steps(s: Skeleton, mask: np.ndarray) -> dict[str, float]:
    """Passi dalla distanza tra le caviglie (massima a ogni appoggio): cadenza e variabilità."""
    d = np.linalg.norm(s["l_ankle"] - s["r_ankle"], axis=1) / leg_length(s)
    d = np.where(mask, d, np.nan)
    if np.isfinite(d).sum() < 2 * FS:
        return {"cadence_spm": np.nan, "step_time_cv": np.nan, "step_len_rel": np.nan}
    filled = np.nan_to_num(d, nan=np.nanmin(d))
    peaks, _ = find_peaks(filled, distance=int(0.25 * FS), prominence=0.05)
    peaks = peaks[mask[peaks]]
    if len(peaks) < 3:
        return {"cadence_spm": np.nan, "step_time_cv": np.nan, "step_len_rel": np.nan}
    intervals = np.diff(peaks) / FS
    intervals = intervals[intervals < 1.5]  # le pause e la svolta non sono passi
    if len(intervals) < 2:
        return {"cadence_spm": np.nan, "step_time_cv": np.nan, "step_len_rel": np.nan}
    return {
        "cadence_spm": float(60 / np.median(intervals)),
        "step_time_cv": float(np.std(intervals) / np.mean(intervals)),
        "step_len_rel": float(np.median(filled[peaks])),
    }


def _rise_features(s: Skeleton, h: np.ndarray, rises: list[tuple[int, int]]) -> dict[str, float]:
    trunk = trunk_inclination(s)
    durations, peaks_v, trunk_max = [], [], []
    v = np.gradient(h) * FS
    for a, b in rises:
        a0 = max(0, a - int(FS))  # il tronco si piega prima che il bacino si alzi
        durations.append((b - a) / FS)
        peaks_v.append(np.max(v[a:b + 1]))
        trunk_max.append(np.max(trunk[a0:b + 1]))
    return {
        "rise_time_s": float(np.median(durations)) if durations else np.nan,
        "rise_peak_vel": float(np.median(peaks_v)) if peaks_v else np.nan,
        "rise_trunk_max_deg": float(np.median(trunk_max)) if trunk_max else np.nan,
    }


def tug(s: Skeleton) -> dict[str, float]:
    h = pelvis_height(s)
    rises, sits, lo, hi = _transitions(h)
    upright = h > lo + 0.75 * (hi - lo)
    f = {
        "upright_time_s": float(upright.sum() / FS) if rises else np.nan,
        "sit_time_s": float(np.median([(b - a) / FS for a, b in sits])) if sits else np.nan,
        **_rise_features(s, h, rises),
        **_steps(s, upright),
    }
    knee = np.nanmax([flexion(s, "l", "knee"), flexion(s, "r", "knee")], axis=0)
    f["knee_flex_walk_p95"] = float(np.nanpercentile(knee[upright], 95)) if upright.any() else np.nan
    return f


def sts5(s: Skeleton) -> dict[str, float]:
    h = pelvis_height(s)
    rises, sits, lo, hi = _transitions(h)
    f = {"sts_cycles": float(len(rises))}
    if len(rises) >= 2:
        starts = [a for a, _ in rises]
        cycle = np.diff(starts) / FS
        f["sts_cycle_s"] = float(np.median(cycle))
        f["sts_cycle_cv"] = float(np.std(cycle) / np.mean(cycle)) if len(cycle) > 1 else np.nan
        # Tempo per 5 alzate stimato dal ciclo medio: regge anche se una ripetizione non è stata riconosciuta.
        f["sts5_time_est_s"] = float(np.median(cycle) * 5)
    else:
        f |= {"sts_cycle_s": np.nan, "sts_cycle_cv": np.nan, "sts5_time_est_s": np.nan}
    f |= _rise_features(s, h, rises)
    f["stand_height_rel"] = float(hi)  # quanto si estende in piedi (ginocchia e anche)
    return f


def stance(s: Skeleton) -> dict[str, float]:
    """Tandem e semi-tandem: oscillazione del bacino sopra la base d'appoggio, sul piano orizzontale."""
    up = up_axis(s)
    rel = s.mid("l_hip", "r_hip") - s.mid("l_ankle", "r_ankle")
    horiz = rel - np.outer(rel @ up, up)
    horiz -= horiz.mean(axis=0)
    # Due assi orizzontali qualsiasi: area e RMS non dipendono da come sono orientati.
    e1 = _unit(np.cross(up, [1.0, 0, 0]) if abs(up[0]) < 0.9 else np.cross(up, [0, 0, 1.0]))
    e2 = np.cross(up, e1)
    xy = np.stack([horiz @ e1, horiz @ e2], axis=1)
    r = np.linalg.norm(xy, axis=1)
    path = np.linalg.norm(np.diff(xy, axis=0), axis=1).sum()
    cov = np.cov(xy.T)
    eig = np.clip(np.linalg.eigvalsh(cov), 0, None)
    return {
        "hold_time_s": float(s.t[-1] - s.t[0]),
        "sway_rms_mm": float(np.sqrt(np.mean(r ** 2)) * 1e3),
        "sway_vel_mm_s": float(path / (s.t[-1] - s.t[0]) * 1e3),
        # Area dell'ellisse di confidenza al 95% (chi² con 2 gradi di libertà = 5.991).
        "sway_area_cm2": float(np.pi * 5.991 * np.sqrt(eig.prod()) * 1e4),
        "trunk_incl_sd_deg": float(np.std(trunk_inclination(s))),
    }


def walk(s: Skeleton) -> dict[str, float]:
    h = pelvis_height(s)
    _, _, lo, hi = _transitions(h)
    upright = h > lo + 0.75 * (hi - lo) if hi - lo >= 0.15 else np.ones(len(h), bool)
    f = _steps(s, upright)
    f["upright_time_s"] = float(upright.sum() / FS)
    return f


EXTRACTORS = {"tug": tug, "sts5": sts5, "tandem": stance, "semi_tandem": stance, "walk3m": walk}


def extract(movement: str, s: Skeleton) -> dict[str, float]:
    """Caratteristiche di un movimento con il prefisso del movimento (es. ``tug.cadence_spm``)."""
    prepared = prepare(s)
    if prepared is None:
        return {}
    return {f"{movement}.{k}": v for k, v in EXTRACTORS[movement](prepared).items()}
