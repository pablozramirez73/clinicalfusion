"""Pre-addestramento su KINECAL: probabilità di aver avuto cadute nell'ultimo anno, dalle caratteristiche del movimento.

Coorte: solo gli over 65 di KINECAL (Non-Faller contro Self-reported-Faller), così il modello non impara a
riconoscere l'età invece del rischio. Età, sesso e misure del corpo restano fuori: l'app non le registra e
il modello deve guardare solo il movimento.

Modello: regressione logistica con penalità L2 sulle caratteristiche standardizzate. Con 57 persone un
modello più complesso imparerebbe il rumore; questo resta leggibile (un peso per caratteristica) e si porta
nell'app come una manciata di numeri.

Valutazione: validazione incrociata stratificata a 5 parti ripetuta 50 volte, con la scelta della penalità
fatta dentro ogni parte (niente informazione dal campione di verifica). AUC con intervallo bootstrap.

Uso: python -m fall_risk.train [--data data/kinecal] [--out models]
"""

from __future__ import annotations

import argparse
import json
from datetime import date
from pathlib import Path

import numpy as np
import pandas as pd
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression, LogisticRegressionCV
from sklearn.metrics import brier_score_loss, roc_auc_score
from sklearn.model_selection import RepeatedStratifiedKFold, StratifiedKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

from . import features as F

# Test dell'app → movimento di KINECAL con le stesse caratteristiche. Il semi-tandem non c'è nell'app: si
# scarica ma non entra nel modello, che deve poter essere calcolato su una rilevazione reale.
APP_TESTS = {"tug": "tug", "5tsts": "sts5", "tandem": "tandem", "10mwt": "walk3m"}
MODEL_MOVEMENTS = tuple(APP_TESTS.values())

# Caratteristiche che non hanno lo stesso significato nell'app: il tandem dell'app dura 10 s (KINECAL 20 s),
# il cammino dell'app è di 10 m in linea retta (KINECAL 3 m con ritorno alla sedia).
EXCLUDED = {"tandem.hold_time_s", "walk3m.upright_time_s"}

POSITIVE_GROUPS = {"FHs", "FHm"}


def load_register(path: Path) -> pd.DataFrame:
    reg = pd.read_csv(path).dropna(axis=1, how="all")
    reg["id"] = reg["part_id"].str.replace("SPPB", "", regex=False)
    reg["age"] = pd.to_numeric(reg["age"].astype(str).str.replace(">", "", regex=False), errors="coerce")
    return reg.set_index("id")


def build_features(skeletons: Path) -> pd.DataFrame:
    rows: dict[str, dict[str, float]] = {}
    for f in sorted(skeletons.glob("*.npz")):
        if f.name.endswith(".part.npz"):
            continue
        part_id, movement = f.stem.split("_", 1)
        if movement not in MODEL_MOVEMENTS:
            continue
        with np.load(f) as d:
            s = F.from_kinect(d["t"], d["points"], list(d["joints"]))
        rows.setdefault(part_id, {}).update(F.extract(movement, s))
    return pd.DataFrame.from_dict(rows, orient="index")


def oof_predictions(X: pd.DataFrame, y: np.ndarray, repeats: int, seed: int) -> np.ndarray:
    """Probabilità fuori campione mediate sulle ripetizioni; la penalità è scelta dentro ogni parte."""
    cv = RepeatedStratifiedKFold(n_splits=5, n_repeats=repeats, random_state=seed)
    total = np.zeros(len(y))
    counts = np.zeros(len(y))
    for train, test in cv.split(X, y):
        model = make_model(inner_cv=True, seed=seed)
        model.fit(X.iloc[train], y[train])
        total[test] += model.predict_proba(X.iloc[test])[:, 1]
        counts[test] += 1
    return total / counts


def make_model(inner_cv: bool, seed: int, c: float = 1.0):
    clf = (
        LogisticRegressionCV(Cs=np.logspace(-3, 1, 13), cv=StratifiedKFold(4, shuffle=True, random_state=seed),
                             scoring="roc_auc", max_iter=5000)
        if inner_cv
        else LogisticRegression(C=c, max_iter=5000)
    )
    return make_pipeline(SimpleImputer(strategy="median"), StandardScaler(), clf)


def bootstrap_auc(y: np.ndarray, p: np.ndarray, n: int = 2000, seed: int = 0) -> tuple[float, float]:
    rng = np.random.default_rng(seed)
    values = []
    for _ in range(n):
        i = rng.integers(0, len(y), len(y))
        if y[i].min() != y[i].max():
            values.append(roc_auc_score(y[i], p[i]))
    return float(np.percentile(values, 2.5)), float(np.percentile(values, 97.5))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", type=Path, default=Path("data/kinecal"))
    parser.add_argument("--out", type=Path, default=Path("models"))
    parser.add_argument("--repeats", type=int, default=50)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    reg = load_register(args.data / "register.csv")
    feats = build_features(args.data / "skeletons")
    feats.to_csv(args.data / "features.csv", index_label="id")

    df = reg.join(feats, how="inner")
    cohort = df[df["group"].isin(POSITIVE_GROUPS | {"NF"})]
    y = cohort["group"].isin(POSITIVE_GROUPS).to_numpy(int)

    columns = [c for c in feats.columns if c not in EXCLUDED and cohort[c].notna().mean() >= 0.7]
    X = cohort[columns].astype(float)
    print(f"Coorte: {len(y)} over 65, {y.sum()} con cadute; {len(columns)} caratteristiche.")

    # Quanto separa ogni caratteristica da sola (AUC > 0.5: valori alti nei caduti).
    univariate = {}
    for c in columns:
        ok = X[c].notna().to_numpy()
        if y[ok].min() != y[ok].max():
            univariate[c] = round(float(roc_auc_score(y[ok], X[c][ok])), 3)

    p = oof_predictions(X, y, args.repeats, args.seed)
    auc = float(roc_auc_score(y, p))
    lo, hi = bootstrap_auc(y, p)

    # Confronto: il solo tempo del TUG, cioè quello che l'app già misura e confronta con la soglia di 12 s.
    baseline_col = "tug.upright_time_s"
    pb = oof_predictions(X[[baseline_col]], y, args.repeats, args.seed)
    auc_baseline = float(roc_auc_score(y, pb))

    # Modello finale su tutta la coorte, con la penalità scelta dalla validazione interna.
    selector = make_model(inner_cv=True, seed=args.seed).fit(X, y)
    c_best = float(selector[-1].C_[0])
    final = make_model(inner_cv=False, seed=args.seed, c=c_best).fit(X, y)
    imputer, scaler, clf = final

    model = {
        "name": "fall_risk",
        "version": "pretrained-kinecal-1",
        "created": date.today().isoformat(),
        "target": "Cadute nell'ultimo anno riferite dal soggetto (over 65): 1 = almeno una caduta.",
        "source": {
            "dataset": "KINECAL v1.0.3, PhysioNet, CC0 1.0",
            "doi": "10.13026/vvkp-ct80",
            "sensor": "Kinect v2, scheletro a 25 punti, 30 fps",
        },
        "app_tests": APP_TESTS,
        "features": columns,
        "impute_median": imputer.statistics_.round(6).tolist(),
        "mean": scaler.mean_.round(6).tolist(),
        "scale": scaler.scale_.round(6).tolist(),
        "coef": clf.coef_[0].round(6).tolist(),
        "intercept": round(float(clf.intercept_[0]), 6),
        "C": c_best,
        "validation": {
            "method": f"5-fold stratificata ripetuta {args.repeats} volte, penalità scelta dentro ogni parte",
            "n": int(len(y)),
            "positives": int(y.sum()),
            "auc": round(auc, 3),
            "auc_ci95": [round(lo, 3), round(hi, 3)],
            "brier": round(float(brier_score_loss(y, p)), 3),
            "auc_tug_time_only": round(auc_baseline, 3),
        },
        "univariate_auc": univariate,
    }
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "fall_risk_kinecal.json").write_text(json.dumps(model, indent=2, ensure_ascii=False) + "\n")

    print(f"AUC fuori campione {auc:.3f} (IC 95% {lo:.3f}–{hi:.3f}); solo tempo del TUG {auc_baseline:.3f}")
    print(f"Brier {model['validation']['brier']}; C scelto {c_best:.3g}")
    weights = sorted(zip(columns, clf.coef_[0]), key=lambda kv: -abs(kv[1]))
    for name, w in weights:
        print(f"  {w:+.3f}  {name}  (AUC da sola {univariate.get(name, float('nan')):.3f})")


if __name__ == "__main__":
    main()
