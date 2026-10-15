"""Taratura e validazione del modello pre-addestrato sulle rilevazioni dell'app.

Il Kinect e MediaPipe non misurano allo stesso modo (le anche del Kinect stanno più in alto, il rumore è
diverso), quindi i valori grezzi delle caratteristiche hanno medie e dispersioni diverse. I passi:

1. **Allineamento** (senza etichette): la standardizzazione usa media e deviazione standard delle
   rilevazioni dell'app invece di quelle di KINECAL. I pesi del modello restano quelli pre-addestrati.
2. **Ricalibrazione** (con etichette): due soli parametri, intercetta e pendenza sul punteggio del modello,
   per riportare le probabilità alla frequenza di cadute della vostra popolazione.
3. **Ristima dei pesi** (facoltativa, con abbastanza dati): i pesi si adattano ai vostri dati ma vengono
   tirati verso quelli pre-addestrati, tanto più quanto meno dati ci sono.

Ogni variante è valutata fuori campione (validazione incrociata stratificata ripetuta): AUC con intervallo
bootstrap, Brier, calibrazione (intercetta e pendenza). Il modello tarato e il rapporto vanno in --out.

Le etichette sono un CSV ``patient_code,faller`` (1 = almeno una caduta negli ultimi 12 mesi, come in
KINECAL), da raccogliere con la stessa domanda usata dallo studio.

Uso: python -m fall_risk.calibrate --features data/app/features.csv --labels data/app/labels.csv
"""

from __future__ import annotations

import argparse
import json
from datetime import date
from pathlib import Path

import numpy as np
import pandas as pd
from scipy.optimize import minimize
from sklearn.metrics import brier_score_loss, roc_auc_score
from sklearn.model_selection import RepeatedStratifiedKFold

from .train import bootstrap_auc

MIN_PER_CLASS = 10
REFIT_MIN_PER_CLASS = 25


def sigmoid(z: np.ndarray) -> np.ndarray:
    return 1 / (1 + np.exp(-z))


def standardize(X: np.ndarray, median: np.ndarray, mean: np.ndarray, scale: np.ndarray) -> np.ndarray:
    X = np.where(np.isnan(X), median, X)
    return (X - mean) / np.where(scale > 0, scale, 1)


def domain_stats(X: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    median = np.nanmedian(X, axis=0)
    filled = np.where(np.isnan(X), median, X)
    return median, filled.mean(axis=0), filled.std(axis=0)


def fit_recalibration(lp: np.ndarray, y: np.ndarray) -> tuple[float, float]:
    """Intercetta e pendenza della ricalibrazione logistica sul punteggio lineare."""
    def loss(ab):
        p = np.clip(sigmoid(ab[0] + ab[1] * lp), 1e-9, 1 - 1e-9)
        return -np.mean(y * np.log(p) + (1 - y) * np.log(1 - p))
    return tuple(minimize(loss, [0.0, 1.0], method="BFGS").x)


def fit_refit(Z: np.ndarray, y: np.ndarray, w0: np.ndarray, b0: float, strength: float) -> tuple[np.ndarray, float]:
    """Pesi ristimati con penalità L2 verso quelli pre-addestrati (non verso zero)."""
    def loss(theta):
        w, b = theta[:-1], theta[-1]
        p = np.clip(sigmoid(Z @ w + b), 1e-9, 1 - 1e-9)
        return -np.mean(y * np.log(p) + (1 - y) * np.log(1 - p)) + strength * np.sum((w - w0) ** 2)
    theta = minimize(loss, np.r_[w0, b0], method="L-BFGS-B").x
    return theta[:-1], float(theta[-1])


def calibration(y: np.ndarray, p: np.ndarray) -> dict[str, float]:
    a, b = fit_recalibration(np.log(np.clip(p, 1e-9, 1 - 1e-9) / np.clip(1 - p, 1e-9, 1)), y)
    return {"calibration_intercept": round(float(a), 3), "calibration_slope": round(float(b), 3)}


def metrics(y: np.ndarray, p: np.ndarray) -> dict:
    lo, hi = bootstrap_auc(y, p)
    return {
        "auc": round(float(roc_auc_score(y, p)), 3),
        "auc_ci95": [round(lo, 3), round(hi, 3)],
        "brier": round(float(brier_score_loss(y, p)), 3),
        "mean_predicted": round(float(p.mean()), 3),
        "observed_rate": round(float(y.mean()), 3),
        **calibration(y, p),
    }


def evaluate(model: dict, X: np.ndarray, y: np.ndarray, repeats: int, seed: int, refit: bool) -> dict[str, dict]:
    w0, b0 = np.array(model["coef"]), float(model["intercept"])
    kinecal = [np.array(model[k]) for k in ("impute_median", "mean", "scale")]

    results = {"pretrained": metrics(y, sigmoid(standardize(X, *kinecal) @ w0 + b0))}

    # L'allineamento non usa le etichette: lo si calcola per ogni parte di addestramento comunque, così la
    # stima resta onesta anche rispetto alla distribuzione delle caratteristiche.
    variants = ["aligned", "recalibrated"] + (["refit"] if refit else [])
    oof = {v: np.zeros(len(y)) for v in variants}
    counts = np.zeros(len(y))
    for train, test in RepeatedStratifiedKFold(n_splits=5, n_repeats=repeats, random_state=seed).split(X, y):
        stats = domain_stats(X[train])
        lp_train = standardize(X[train], *stats) @ w0
        lp_test = standardize(X[test], *stats) @ w0
        oof["aligned"][test] += sigmoid(lp_test + b0)
        a, b = fit_recalibration(lp_train, y[train])
        oof["recalibrated"][test] += sigmoid(a + b * lp_test)
        if refit:
            w, c = fit_refit(standardize(X[train], *stats), y[train], w0, b0, strength=1.0 / len(train))
            oof["refit"][test] += sigmoid(standardize(X[test], *stats) @ w + c)
        counts[test] += 1
    for v in variants:
        results[v] = metrics(y, oof[v] / counts)
    return results


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", type=Path, default=Path("models/fall_risk_gstride.json"))
    parser.add_argument("--features", type=Path, required=True)
    parser.add_argument("--labels", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=Path("models"))
    parser.add_argument("--repeats", type=int, default=50)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    model = json.loads(args.model.read_text())
    feats = pd.read_csv(args.features, index_col="patient_code")
    labels = pd.read_csv(args.labels, index_col="patient_code")["faller"].astype(int)
    df = feats.join(labels, how="inner")
    missing = [c for c in model["features"] if c not in df.columns]
    for c in missing:
        df[c] = np.nan  # test mai eseguito: la caratteristica prende la mediana
    X = df[model["features"]].to_numpy(float)
    y = df["faller"].to_numpy(int)

    pos, neg = int(y.sum()), int(len(y) - y.sum())
    print(f"{len(y)} pazienti con etichetta: {pos} con cadute, {neg} senza.")
    if missing:
        print(f"Caratteristiche assenti in tutte le rilevazioni: {', '.join(missing)}")
    if min(pos, neg) < MIN_PER_CLASS:
        raise SystemExit(
            f"Servono almeno {MIN_PER_CLASS} pazienti per gruppo per una stima che abbia senso "
            f"(ora {pos} con cadute e {neg} senza). Nessun modello tarato è stato scritto.")

    refit = min(pos, neg) >= REFIT_MIN_PER_CLASS
    results = evaluate(model, X, y, args.repeats, args.seed, refit)
    for name, m in results.items():
        print(f"{name:13s} AUC {m['auc']:.3f} {m['auc_ci95']}  Brier {m['brier']:.3f}  "
              f"calibrazione {m['calibration_intercept']:+.2f} / {m['calibration_slope']:.2f}")

    # Modello finale: allineato e ricalibrato su tutti i dati (ristimato solo se i dati bastano).
    w0, b0 = np.array(model["coef"]), float(model["intercept"])
    median, mean, scale = domain_stats(X)
    Z = standardize(X, median, mean, scale)
    if refit:
        w, b = fit_refit(Z, y, w0, b0, strength=1.0 / len(y))
        variant = "refit"
    else:
        a, s = fit_recalibration(Z @ w0, y)
        w, b = w0 * s, a
        variant = "recalibrated"

    calibrated = model | {
        "version": f"calibrated-{date.today().isoformat()}",
        "created": date.today().isoformat(),
        "impute_median": median.round(6).tolist(),
        "mean": mean.round(6).tolist(),
        "scale": scale.round(6).tolist(),
        "coef": np.round(w, 6).tolist(),
        "intercept": round(float(b), 6),
        "calibration": {"variant": variant, "n": len(y), "positives": pos, "results": results},
    }
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "fall_risk_calibrated.json").write_text(json.dumps(calibrated, indent=2, ensure_ascii=False) + "\n")
    print(f"Modello tarato ({variant}) → {args.out / 'fall_risk_calibrated.json'}")


if __name__ == "__main__":
    main()
