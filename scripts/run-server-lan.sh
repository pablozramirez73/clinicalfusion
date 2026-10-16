#!/usr/bin/env bash
# Avvia server e dashboard raggiungibili dalla rete locale (es. dal telefono sulla stessa Wi-Fi).
#
# Le credenziali di sviluppo sono scritte nella documentazione: in rete non devono valere. Al primo avvio lo
# script ne genera di nuove (password della dashboard e chiave del dispositivo): il server riceve solo gli hash,
# da .server-lan.env; credenziali in chiaro e hash vanno in entrata.log e vengono mostrati a ogni avvio.
# Entrambi i file sono locali, leggibili solo dal proprio utente ed esclusi da git.
# Per rigenerare le credenziali: cancellare .server-lan.env (o entrata.log).
#
# Traffico in HTTPS con certificato autofirmato (l'app lo riconosce dall'impronta). Per un uso reale vedi
# docs/server.md (certificato di un'autorità, produzione).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SERVER="$ROOT/src/ClinicalSensorFusion.Server"
ENV_FILE="$ROOT/.server-lan.env"
ENTRY_LOG="$ROOT/entrata.log"
PORT="${PORT:-5180}"
USER_NAME="demo"
DEVICE_NAME="telefono-lan"

cd "$ROOT"

if [[ ! -f "$SERVER/wwwroot/index.html" ]]; then
  echo "Compilo la dashboard…"
  (cd src/ClinicalSensorFusion.Dashboard && npm ci --silent && npm run build --silent)
fi

dotnet build "$SERVER" -v:q -nologo >/dev/null
admin() { dotnet run --project "$SERVER" --no-build --no-launch-profile -- "$@" | tail -n 2; }

address="$(ipconfig getifaddr en0 2>/dev/null || hostname -I 2>/dev/null | awk '{print $1}' || true)"
url="https://${address:-<indirizzo di questo computer>}:$PORT"

# HTTPS anche in rete locale, con un certificato autofirmato generato qui. Non è firmato da un'autorità:
# l'app lo accetta solo se coincide l'impronta della chiave pubblica (SHA-256), da inserire nelle
# Impostazioni accanto alla chiave del dispositivo. Il browser della dashboard mostra un avviso la prima volta.
CERT_DIR="$ROOT/.server-lan"
CERT="$CERT_DIR/server.crt"
CERT_KEY="$CERT_DIR/server.key"
host_name="$(scutil --get LocalHostName 2>/dev/null || hostname -s)"
san="DNS:localhost,DNS:${host_name}.local,IP:127.0.0.1${address:+,IP:$address}"
if [[ ! -f "$CERT" || ! -f "$CERT_KEY" ]] || ! openssl x509 -in "$CERT" -noout -ext subjectAltName 2>/dev/null | grep -q "${address:-127.0.0.1}"; then
  # Nuovo certificato anche se l'indirizzo è cambiato; la chiave resta, quindi l'impronta nell'app resta valida.
  (umask 077 && mkdir -p "$CERT_DIR")
  [[ -f "$CERT_KEY" ]] || (umask 077 && openssl ecparam -name prime256v1 -genkey -noout -out "$CERT_KEY")
  openssl req -new -x509 -key "$CERT_KEY" -out "$CERT" -days 825 -subj "/CN=SensorFusion Clinic (rete locale)" \
    -addext "subjectAltName=$san" -addext "extendedKeyUsage=serverAuth" 2>/dev/null
fi
pin="$(openssl x509 -in "$CERT" -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | openssl base64 -A)"
export Kestrel__Certificates__Default__Path="$CERT"
export Kestrel__Certificates__Default__KeyPath="$CERT_KEY"

# Il server di sviluppo si aspetta l'utente "postgres" di docker compose; un PostgreSQL installato sul
# computer (es. Homebrew) di solito ha solo l'utente di sistema.
connection_string() {
  if command -v psql >/dev/null && ! psql -h localhost -U postgres -d postgres -Atqc 'select 1' >/dev/null 2>&1 \
     && psql -h localhost -U "$(id -un)" -d postgres -Atqc 'select 1' >/dev/null 2>&1; then
    echo "export ConnectionStrings__Clinical=\"Host=localhost;Database=clinical_dev;Username=$(id -un)\""
  else
    echo "# PostgreSQL diverso da quello di docker compose (utente \"postgres\")? Togliere il commento e adattare:"
    echo "# export ConnectionStrings__Clinical=\"Host=localhost;Database=clinical_dev;Username=$(id -un)\""
  fi
}

# auth.log è il nome usato da una versione precedente dello script: se c'è, le credenziali restano quelle.
if [[ ! -f "$ENTRY_LOG" && -f "$ROOT/auth.log" && -f "$ENV_FILE" ]]; then
  mv "$ROOT/auth.log" "$ENTRY_LOG"
fi

if [[ ! -f "$ENV_FILE" || ! -f "$ENTRY_LOG" ]]; then
  [[ -f "$ENV_FILE" ]] && echo "entrata.log mancante: le credenziali precedenti non sono recuperabili, ne genero di nuove."

  password="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-16)"
  password_hash="$(admin hash-password "$password" | tail -n 1)"
  device="$(admin new-device-key "$DEVICE_NAME")"
  device_key="$(sed -n 's/.*volta): //p' <<<"$device")"
  device_hash="$(sed -n 's/.*"KeySha256": "\([0-9a-f]*\)".*/\1/p' <<<"$device")"
  if [[ -z "$password_hash" || -z "$device_key" || -z "$device_hash" ]]; then
    echo "Generazione delle credenziali fallita." >&2
    exit 1
  fi

  (umask 077 && cat > "$ENV_FILE" <<ENV
# Generato da scripts/run-server-lan.sh. Contiene solo hash; non va nel repository.
export Ingest__Devices__0__Name="$DEVICE_NAME"
export Ingest__Devices__0__KeySha256="$device_hash"
export Dashboard__Users__0__PasswordHash="$password_hash"
$(connection_string)
ENV
  )

  (umask 077 && cat > "$ENTRY_LOG" <<LOG
# Credenziali del server di sviluppo in rete locale, generate da scripts/run-server-lan.sh.
# CONTIENE SEGRETI IN CHIARO: file locale (permessi 600), escluso da git. Non condividerlo.
generate: $(date '+%Y-%m-%d %H:%M:%S')

[Dashboard] login nel browser
  utente:             $USER_NAME
  password:           $password
  hash della password (Dashboard__Users__0__PasswordHash): $password_hash

[App] Impostazioni → Server clinico
  indirizzo:          (vedi le righe "avvio" più sotto)
  chiave dispositivo: $device_key
  nome dispositivo:   $DEVICE_NAME
  SHA-256 della chiave (Ingest__Devices__0__KeySha256): $device_hash

LOG
  )
fi

# shellcheck disable=SC1090
source "$ENV_FILE"
chmod 600 "$ENTRY_LOG"
echo "avvio: $(date '+%Y-%m-%d %H:%M:%S')  indirizzo del server: $url  impronta del certificato: $pin" >> "$ENTRY_LOG"

echo
echo "Credenziali (anche in $ENTRY_LOG):"
grep -v '^#' "$ENTRY_LOG" | sed '/^avvio: /d'
echo "  indirizzo del server da inserire nell'app: $url"
echo "  impronta del certificato da inserire nell'app: $pin"
echo "  IA tramite il server (indirizzo Ollama nell'app): ${url}/api/v1/ai/"
echo

exec dotnet run --project "$SERVER" --no-build --launch-profile http -- --urls "https://0.0.0.0:$PORT"
