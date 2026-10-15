"""Pre-addestramento su GSTRIDE (Zenodo, CC BY 4.0): cadute nell'ultimo anno da misure che anche l'app ricava.

GSTRIDE: 163 anziani tra 70 e 98 anni, 86 caduti nell'ultimo anno. Il sensore è un IMU sul piede e i test
sono quelli della valutazione geriatrica. Si usano solo le misure che l'app produce anch'essa:

- tempo del TUG (test TUG dell'app);
- velocità del cammino (dal 10MWT dell'app; in GSTRIDE è sui 4 m);
- punteggio SPPB dell'alzata dalla sedia, 0–4 (l'app lo ricava dal tempo del 5TSTS con le soglie SPPB);
- punteggio SPPB dell'equilibrio, 0–4 (nell'app c'è solo lo stadio del tandem: corrispondenza parziale);
- ritmo del passo: cadenza, durata e variabilità del passo (l'app li stima dai punti delle caviglie).

Restano fuori le misure che l'app non ha: lunghezza del passo e velocità dall'IMU, angoli del piede, forza
della mano, questionari, età e sesso. Vengono valutate a parte, solo per sapere quanto ci si perde.

Uso: python -m fall_risk.gstride_train [--register data/gstride/Database_register.csv] [--out models]
"""

from __future__ import annotations

import argparse
import json
from datetime import date
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.metrics import brier_score_loss, roc_auc_score

from .calibrate import calibration
from .train import bootstrap_auc, make_model, oof_predictions

# La penalità si sceglie con la log-loss e non con l'AUC: il modello finisce nell'app, dove contano anche le
# probabilità e non solo l'ordine dei pazienti. Scelta per AUC, la penalità schiaccia tutto verso il 50%.
SCORING = "neg_log_loss"

APP_SET = "misure cliniche + ritmo del passo (tutto ciò che l'app ricava)"

FALLS = "Falls during the last year - prior to test (YES/NO)"

# Nome usato qui → colonna di GSTRIDE.
CLINICAL = {
    "tug_time_s": "Time duration TUG test (s)",
    "gait_speed_m_s": "Gait speed (m/s)",
    "sppb_chair_index": "Chair stand test (index)",
    "sppb_balance_index": "Balance test (index)",
}
RHYTHM = {
    "cadence_strides_min": "Cadence - Avg. (strides/min)",
    "stride_time_s": "Stride time - Avg. (s)",
    "stride_time_cv": None,  # STD / media, calcolata sotto
}
# Solo per confronto: misure che l'app non ha.
NOT_IN_APP = {
    "age": "Age (range of years)",
    "short_fes_i": "Short FES-I assessment (index)",
    "frailty_index": "Frailty assessment (index)",
    "grip_kg": "Manual force  (kg)",
    "stride_length_m": "Stride Length - Avg. (m)",
    "clearance_m": "Clearance - Avg. (m)",
}


def number(s: pd.Series) -> pd.Series:
    return pd.to_numeric(s.astype(str).str.strip().str.replace(",", ".", regex=False), errors="coerce")


def load(path: Path) -> tuple[pd.DataFrame, np.ndarray]:
    raw = pd.read_csv(path, sep=";", header=1, skiprows=[2], encoding="latin-1", decimal=",")
    y = raw[FALLS].astype(str).str.strip().str.upper().map({"YES": 1, "NO": 0})
    df = pd.DataFrame(index=raw.index)
    for name, col in (CLINICAL | RHYTHM | NOT_IN_APP).items():
        if col:
            df[name] = number(raw[col])
    df["stride_time_cv"] = number(raw["Stride time - STD (s)"]) / df["stride_time_s"]
    # L'età è una fascia ("80-84"): si usa il centro della fascia.
    ages = raw[NOT_IN_APP["age"]].astype(str).str.extract(r"(\d+)\D+(\d+)").astype(float)
    df["age"] = ages.mean(axis=1)
    keep = y.notna()
    return df[keep].reset_index(drop=True), y[keep].to_numpy(int)


def evaluate(X: pd.DataFrame, y: np.ndarray, repeats: int, seed: int) -> dict:
    p = oof_predictions(X, y, repeats, seed, scoring=SCORING)
    lo, hi = bootstrap_auc(y, p)
    return {"features": list(X.columns), "auc": round(float(roc_auc_score(y, p)), 3),
            "auc_ci95": [round(lo, 3), round(hi, 3)], "brier": round(float(brier_score_loss(y, p)), 3),
            **calibration(y, p)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--register", type=Path, default=Path("data/gstride/Database_register.csv"))
    parser.add_argument("--out", type=Path, default=Path("models"))
    parser.add_argument("--repeats", type=int, default=50)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    df, y = load(args.register)
    print(f"GSTRIDE: {len(y)} persone, {y.sum()} con cadute nell'ultimo anno.")

    univariate = {}
    for c in df.columns:
        ok = df[c].notna().to_numpy()
        auc = roc_auc_score(y[ok], df[c][ok])
        univariate[c] = round(float(max(auc, 1 - auc)), 3)  # direzione qualsiasi: quanto separa
        print(f"  {c:22s} AUC da sola {univariate[c]:.3f}  (mancanti {int((~ok).sum())})")

    app_clinical = list(CLINICAL)
    app_all = app_clinical + list(RHYTHM)
    sets = {
        "solo tempo del TUG": ["tug_time_s"],
        "misure cliniche dell'app": app_clinical,
        APP_SET: app_all,
        "riferimento: tutto, anche ciò che l'app non ha": app_all + list(NOT_IN_APP),
    }
    results = {}
    for name, cols in sets.items():
        results[name] = evaluate(df[cols], y, args.repeats, args.seed)
        r = results[name]
        print(f"{name:66s} AUC {r['auc']:.3f} (IC 95% {r['auc_ci95'][0]:.3f}–{r['auc_ci95'][1]:.3f})  "
              f"Brier {r['brier']:.3f}  calibrazione {r['calibration_intercept']:+.2f} / {r['calibration_slope']:.2f}")

    # Modello finale: solo ciò che l'app ricava.
    X = df[app_all]
    selector = make_model(inner_cv=True, seed=args.seed, scoring=SCORING).fit(X, y)
    c_best = float(selector[-1].C_[0])
    imputer, scaler, clf = make_model(inner_cv=False, seed=args.seed, c=c_best).fit(X, y)
    model = {
        "name": "fall_risk",
        "version": "pretrained-gstride-2",
        "created": date.today().isoformat(),
        "target": "Cadute nell'ultimo anno riferite dal soggetto (anziani 70–98 anni): 1 = almeno una caduta.",
        "source": {"dataset": "GSTRIDE, Zenodo, CC BY 4.0", "doi": "10.5281/zenodo.8003441",
                   "sensor": "IMU sul piede; test clinici cronometrati"},
        "features": app_all,
        "impute_median": imputer.statistics_.round(6).tolist(),
        "mean": scaler.mean_.round(6).tolist(),
        "scale": scaler.scale_.round(6).tolist(),
        "coef": clf.coef_[0].round(6).tolist(),
        "intercept": round(float(clf.intercept_[0]), 6),
        "C": c_best,
        "validation": {"auc": results[APP_SET]["auc"], "auc_ci95": results[APP_SET]["auc_ci95"],
                       "method": f"5-fold stratificata ripetuta {args.repeats} volte, penalità scelta dentro ogni parte (log-loss)",
                       "n": int(len(y)), "positives": int(y.sum()), "by_feature_set": results},
        "univariate_auc": univariate,
    }
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "fall_risk_gstride.json").write_text(json.dumps(model, indent=2, ensure_ascii=False) + "\n")
    print("Pesi del modello finale (caratteristiche standardizzate):")
    for name, w in sorted(zip(app_all, clf.coef_[0]), key=lambda kv: -abs(kv[1])):
        print(f"  {w:+.3f}  {name}")


if __name__ == "__main__":
    main()
