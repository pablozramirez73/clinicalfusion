"""Scarica da KINECAL (PhysioNet, CC0) solo gli scheletri dei movimenti che corrispondono ai test dell'app.

Il dataset completo pesa 180 GB, quasi tutti di immagini di profondità: qui servono solo i 25 punti 3D del
Kinect, un file di testo per fotogramma. I fotogrammi di una registrazione vengono riuniti in un unico .npz
(tempi in secondi, punti [T, 25, 3] in metri, maschera dei punti tracciati), così su disco restano poche
centinaia di file invece di centinaia di migliaia.

Fonte: la copia pubblica di PhysioNet su S3 (bucket physionet-open), che non richiede credenziali ed è molto
più veloce del sito per elencare i file.

Uso: python -m fall_risk.kinecal_download [--out data/kinecal] [--workers 48]
"""

from __future__ import annotations

import argparse
import concurrent.futures as cf
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

BUCKET = "https://physionet-open.s3.amazonaws.com"
ROOT = "kinecal/1.0.3/"
NS = {"s3": "http://s3.amazonaws.com/doc/2006-03-01/"}

# Cartelle di KINECAL → nome del movimento usato qui. Il 3m walk corrisponde al test del cammino.
MOVEMENTS = {
    "Get-Up-And-Go-Front-View": "tug",
    "STS-5": "sts5",
    "Tandem-Balance": "tandem",
    "Semi-Tandem-Balance": "semi_tandem",
    "3m-walk-Front-View": "walk3m",
}

# Ordine dei 25 punti del Kinect v2, come nei file di KINECAL.
KINECT_JOINTS = [
    "SpineBase", "SpineMid", "Neck", "Head", "ShoulderLeft", "ElbowLeft", "WristLeft", "HandLeft",
    "ShoulderRight", "ElbowRight", "WristRight", "HandRight", "HipLeft", "KneeLeft", "AnkleLeft", "FootLeft",
    "HipRight", "KneeRight", "AnkleRight", "FootRight", "SpineShoulder", "HandTipLeft", "ThumbLeft",
    "HandTipRight", "ThumbRight",
]
_INDEX = {name: i for i, name in enumerate(KINECT_JOINTS)}


def _get(url: str, attempts: int = 4) -> bytes:
    for attempt in range(attempts):
        try:
            with urllib.request.urlopen(url, timeout=30) as r:
                return r.read()
        except OSError:
            if attempt == attempts - 1:
                raise
    raise AssertionError("unreachable")


def list_keys(prefix: str, delimiter: str | None = None) -> tuple[list[str], list[str]]:
    """Chiavi e sottocartelle sotto un prefisso (ListObjectsV2, con paginazione)."""
    keys: list[str] = []
    prefixes: list[str] = []
    token: str | None = None
    while True:
        query = {"list-type": "2", "prefix": prefix, "max-keys": "1000"}
        if delimiter:
            query["delimiter"] = delimiter
        if token:
            query["continuation-token"] = token
        root = ET.fromstring(_get(f"{BUCKET}/?{urllib.parse.urlencode(query)}"))
        keys += [k.text for k in root.findall("s3:Contents/s3:Key", NS) if k.text]
        prefixes += [p.text for p in root.findall("s3:CommonPrefixes/s3:Prefix", NS) if p.text]
        if root.findtext("s3:IsTruncated", namespaces=NS) != "true":
            return keys, prefixes
        token = root.findtext("s3:NextContinuationToken", namespaces=NS)


def parse_frame(text: str) -> tuple[np.ndarray, np.ndarray]:
    """Un fotogramma: punti [25, 3] (NaN se assenti) e maschera dei punti tracciati (non dedotti)."""
    points = np.full((len(KINECT_JOINTS), 3), np.nan, dtype=np.float32)
    tracked = np.zeros(len(KINECT_JOINTS), dtype=bool)
    for line in text.splitlines():
        parts = line.split()
        if len(parts) < 5 or parts[0] not in _INDEX:
            continue
        i = _INDEX[parts[0]]
        points[i] = [float(v) for v in parts[2:5]]
        tracked[i] = parts[1] == "Tracked"
    return points, tracked


def fetch_recording(skel_prefix: str, out: Path, pool: cf.ThreadPoolExecutor) -> int:
    keys, _ = list_keys(skel_prefix)
    # Il nome del file è il clock tick del Kinect in ms, a volte preceduto dall'id del corpo seguito
    # ("<corpo>_<tick>"): se nella registrazione compaiono più corpi si tiene quello con più fotogrammi.
    parsed = [(k, *Path(k).stem.rpartition("_")[::2]) for k in keys if k.endswith(".txt")]
    if not parsed:
        return 0
    bodies = [body for _, body, _ in parsed]
    main_body = max(set(bodies), key=bodies.count)
    chosen = sorted((int(tick), k) for k, body, tick in parsed if body == main_body)
    keys = [k for _, k in chosen]
    texts = list(pool.map(lambda k: _get(f"{BUCKET}/{k}").decode("utf-8", "replace"), keys))
    ticks = np.array([tick - chosen[0][0] for tick, _ in chosen], dtype=np.int64)
    frames = [parse_frame(t) for t in texts]
    np.savez_compressed(
        out,
        t=(ticks - ticks[0]) / 1e3,
        points=np.stack([f[0] for f in frames]),
        tracked=np.stack([f[1] for f in frames]),
        joints=np.array(KINECT_JOINTS),
    )
    return len(keys)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, default=Path("data/kinecal"))
    parser.add_argument("--workers", type=int, default=48)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)

    register = args.out / "register.csv"
    if not register.exists():
        register.write_bytes(_get(f"{BUCKET}/{ROOT}register.csv"))

    _, participants = list_keys(f"{ROOT}kinecal/", delimiter="/")
    print(f"{len(participants)} partecipanti", flush=True)
    (args.out / "skeletons").mkdir(exist_ok=True)
    with cf.ThreadPoolExecutor(args.workers) as pool, cf.ThreadPoolExecutor(8) as recordings_pool:
        todo: list[tuple[str, Path]] = []
        for recordings in pool.map(lambda p: list_keys(p, delimiter="/")[1], sorted(participants)):
            for rec in recordings:
                folder = rec.rstrip("/").rsplit("/", 1)[-1]  # es. "10_STS-5"
                part_id, _, movement = folder.partition("_")
                name = MOVEMENTS.get(movement)
                target = args.out / "skeletons" / f"{part_id}_{name}.npz"
                if name is not None and not _readable(target):
                    todo.append((f"{rec}skel/", target))
        print(f"{len(todo)} registrazioni da scaricare", flush=True)

        def one(job: tuple[str, Path]) -> str:
            prefix, target = job
            # Scrittura atomica: un download interrotto non lascia un file troncato che sembri completo.
            partial = target.with_suffix(".part.npz")
            frames = fetch_recording(prefix, partial, pool)
            if frames:
                partial.replace(target)
            return f"{target.stem}: {frames} fotogrammi"

        for n, message in enumerate(recordings_pool.map(one, todo), 1):
            print(f"[{n}/{len(todo)}] {message}", flush=True)


def _readable(path: Path) -> bool:
    try:
        with np.load(path) as d:
            return len(d["t"]) > 0
    except (OSError, ValueError, KeyError, EOFError):
        return False


if __name__ == "__main__":
    sys.exit(main())
