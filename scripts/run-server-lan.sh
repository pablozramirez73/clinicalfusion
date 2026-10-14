#!/usr/bin/env bash
# Avvia server e dashboard raggiungibili dalla rete locale (es. dal telefono sulla stessa Wi-Fi).
#
# Le credenziali di sviluppo sono scritte nella documentazione: in rete non devono valere. Al primo avvio lo
# script ne genera di nuove (password della dashboard e chiave del dispositivo), le mostra una sola volta e
# salva solo i relativi hash in .server-lan.env, file locale escluso da git. Per rigenerarle: cancellare il file.
#
# Traffico in HTTP non cifrato: solo su reti fidate. Per un uso reale vedi docs/server.md (HTTPS, produzione).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SERVER="$ROOT/src/ClinicalSensorFusion.Server"
ENV_FILE="$ROOT/.server-lan.env"
PORT="${PORT:-5180}"

cd "$ROOT"

if [[ ! -f "$SERVER/wwwroot/index.html" ]]; then
  echo "Compilo la dashboard…"
  (cd src/ClinicalSensorFusion.Dashboard && npm ci --silent && npm run build --silent)
fi

dotnet build "$SERVER" -v:q -nologo >/dev/null
admin() { dotnet run --project "$SERVER" --no-build --no-launch-profile -- "$@" | tail -n 2; }

if [[ ! -f "$ENV_FILE" ]]; then
  password="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-16)"
  password_hash="$(admin hash-password "$password" | tail -n 1)"
  device="$(admin new-device-key telefono-lan)"
  device_key="$(sed -n 's/.*volta): //p' <<<"$device")"
  device_hash="$(sed -n 's/.*"KeySha256": "\([0-9a-f]*\)".*/\1/p' <<<"$device")"
  if [[ -z "$password_hash" || -z "$device_key" || -z "$device_hash" ]]; then
    echo "Generazione delle credenziali fallita." >&2
    exit 1
  fi

  (umask 077 && cat > "$ENV_FILE" <<ENV
# Generato da scripts/run-server-lan.sh. Contiene solo hash; non va nel repository.
export Ingest__Devices__0__Name="telefono-lan"
export Ingest__Devices__0__KeySha256="$device_hash"
export Dashboard__Users__0__PasswordHash="$password_hash"
# PostgreSQL diverso da quello di docker compose (utente "postgres")? Togliere il commento e adattare:
# export ConnectionStrings__Clinical="Host=localhost;Database=clinical_dev;Username=$(id -un)"
ENV
  )

  echo
  echo "Credenziali nuove (mostrate solo ora, annotarle):"
  echo "  dashboard: utente demo, password $password"
  echo "  chiave del dispositivo da inserire nell'app: $device_key"
  echo
fi

# shellcheck disable=SC1090
source "$ENV_FILE"

address="$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | awk '{print $1}' || true)"
echo "Server sulla rete locale: http://${address:-<indirizzo di questo computer>}:$PORT"

exec dotnet run --project "$SERVER" --no-build --launch-profile http -- --urls "http://0.0.0.0:$PORT"
