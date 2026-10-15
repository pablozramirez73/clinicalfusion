"""Scarica dal server clinico le rilevazioni dei test e ne calcola le caratteristiche, una riga per paziente.

Per ogni paziente e ogni test (TUG, 5TSTS, tandem, 10MWT) usa la rilevazione più recente. I punti 3D
arrivano dall'endpoint di replay della posa, già raddrizzati; sono gli stessi 33 punti di MediaPipe che
l'app salva nello stream PoseLandmarks3D.

Il tandem dell'app dura 10 s e la rilevazione comprende la spiegazione iniziale: se ne usano gli ultimi 10 s,
cioè la prova vera e propria (la rilevazione si chiude da sola alla fine del tempo).

Le credenziali sono quelle della dashboard, lette dalle variabili d'ambiente CSF_USER e CSF_PASSWORD.
I dati scaricati restano in locale (data/app, esclusa da git): sono dati sanitari, anche se pseudonimi.

Uso: CSF_USER=... CSF_PASSWORD=... python -m fall_risk.app_export --server http://localhost:5180
"""

from __future__ import annotations

import argparse
import json
import os
import urllib.request
from pathlib import Path

import numpy as np
import pandas as pd

from . import features as F
from .train import APP_TESTS

TANDEM_SECONDS = 10.0


class Api:
    def __init__(self, server: str, user: str, password: str) -> None:
        self.base = server.rstrip("/") + "/api/v1"
        body = json.dumps({"userName": user, "password": password}).encode()
        req = urllib.request.Request(f"{self.base}/auth/token", body, {"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=30) as r:
            self.token = json.load(r)["access_token"]

    def get(self, path: str):
        req = urllib.request.Request(f"{self.base}{path}", headers={"Authorization": f"Bearer {self.token}"})
        with urllib.request.urlopen(req, timeout=120) as r:
            return json.load(r)


def pose_features(movement: str, replay: dict) -> dict[str, float]:
    frames = replay.get("frames") or []
    if not frames:
        return {}
    t = np.array([f["t"] for f in frames], float)
    pts = np.array([[np.nan if v is None else v for v in f["points"]] for f in frames], float)
    if movement == "tandem":
        keep = t >= t[-1] - TANDEM_SECONDS
        t, pts = t[keep], pts[keep]
    # Il replay è raddrizzato con Y verso il basso (convenzione dell'immagine): la verticale per le
    # caratteristiche è comunque stimata dai dati, quindi il verso non conta.
    return F.extract(movement, F.from_mediapipe(t, pts))


def sppb_chair_index(seconds: float | None) -> float:
    """Punteggio SPPB dell'alzata (0–4) dal tempo delle 5 alzate, con le soglie di Guralnik."""
    if seconds is None or not np.isfinite(seconds) or seconds > 60:
        return 0.0
    return 4.0 if seconds <= 11.19 else 3.0 if seconds <= 13.69 else 2.0 if seconds <= 16.69 else 1.0


def sppb_balance_index(tandem_seconds: float | None) -> float:
    """Punteggio SPPB dell'equilibrio (0–4) dal solo tandem, l'unico stadio che l'app esegue.

    Approssimazione: si presume che chi regge il tandem regga anche piedi uniti e semi-tandem (1 + 1 punto);
    il tandem vale 2 punti a 10 s, 1 punto da 3 a 10 s, 0 sotto i 3 s.
    """
    if tandem_seconds is None or not np.isfinite(tandem_seconds):
        return np.nan
    return 2.0 + (2.0 if tandem_seconds >= 10 else 1.0 if tandem_seconds >= 3 else 0.0)


def gstride_features(outcomes: dict[str, float], pose: dict[str, float]) -> dict[str, float]:
    """Le misure del modello pre-addestrato su GSTRIDE, ricavate dagli esiti dei test e dai passi.

    GSTRIDE conta le falcate (due passi) con un IMU sul piede; qui i passi vengono dalla distanza tra le
    caviglie nel 10MWT: cadenza in falcate = passi / 2, durata della falcata = 120 / passi al minuto, e la
    variabilità del passo fa da stima di quella della falcata.
    """
    cadence = pose.get("walk3m.cadence_spm", np.nan)
    return {
        "tug_time_s": outcomes.get("tug", np.nan),
        "gait_speed_m_s": outcomes.get("10mwt", np.nan),
        "sppb_chair_index": sppb_chair_index(outcomes.get("5tsts")) if "5tsts" in outcomes else np.nan,
        "sppb_balance_index": sppb_balance_index(outcomes.get("tandem")),
        "cadence_strides_min": cadence / 2,
        "stride_time_s": 120 / cadence if cadence and np.isfinite(cadence) else np.nan,
        "stride_time_cv": pose.get("walk3m.step_time_cv", np.nan),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", required=True)
    parser.add_argument("--out", type=Path, default=Path("data/app"))
    parser.add_argument("--take", type=int, default=500)
    args = parser.parse_args()

    api = Api(args.server, os.environ["CSF_USER"], os.environ["CSF_PASSWORD"])
    args.out.mkdir(parents=True, exist_ok=True)
    cache = args.out / "sessions"
    cache.mkdir(exist_ok=True)

    # (paziente, test) → (inizio, id, valore dell'esito: secondi o m/s secondo il test)
    latest: dict[tuple[str, str], tuple[str, str, float | None]] = {}
    for item in api.get(f"/sessions?take={args.take}"):
        detail = api.get(f"/sessions/{item['id']}")
        outcome = (detail.get("summary") or {}).get("test") or {}
        test = outcome.get("testId") or ""
        if test not in APP_TESTS:
            continue
        key = (detail["patientCode"], test)
        if key not in latest or detail["startedAt"] > latest[key][0]:
            latest[key] = (detail["startedAt"], item["id"], outcome.get("value"))

    rows: dict[str, dict[str, float]] = {}
    outcomes: dict[str, dict[str, float]] = {}
    for (patient, test), (_, session_id, value) in sorted(latest.items()):
        movement = APP_TESTS[test]
        path = cache / f"{session_id}.json"
        if not path.exists():
            path.write_text(json.dumps(api.get(f"/sessions/{session_id}/pose?maxFrames=5000")))
        feats = pose_features(movement, json.loads(path.read_text()))
        rows.setdefault(patient, {}).update(feats)
        if value is not None:
            outcomes.setdefault(patient, {})[test] = float(value)
        print(f"{patient} {test}: esito {value}, {len(feats)} caratteristiche dalla posa")

    for patient in rows.keys() | outcomes.keys():
        rows[patient] = rows.get(patient, {}) | gstride_features(outcomes.get(patient, {}), rows.get(patient, {}))

    table = pd.DataFrame.from_dict(rows, orient="index")
    table.to_csv(args.out / "features.csv", index_label="patient_code")
    print(f"{len(table)} pazienti → {args.out / 'features.csv'}")


if __name__ == "__main__":
    main()
