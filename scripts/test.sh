#!/usr/bin/env bash
# Esegue tutti i test: Core, server (integrazione su PostgreSQL reale) e dashboard.
#
# Il server ha bisogno di un PostgreSQL in cui creare ed eliminare database temporanei (csf_test_*).
# Per default usa quello di docker compose (utente "postgres"); con un'installazione locale diversa:
#   CSF_TEST_POSTGRES="Host=localhost;Username=$(id -un);Database=postgres" ./scripts/test.sh
# "Database=" deve indicare un database esistente: serve per creare e poi eliminare quelli di prova.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
export CSF_TEST_POSTGRES="${CSF_TEST_POSTGRES:-Host=localhost;Username=postgres;Database=postgres}"

echo "== Core"
dotnet test --project tests/ClinicalSensorFusion.Core.Tests

echo "== Server (PostgreSQL: ${CSF_TEST_POSTGRES})"
dotnet test --project tests/ClinicalSensorFusion.Server.Tests

echo "== Dashboard"
(cd src/ClinicalSensorFusion.Dashboard && { [[ -d node_modules ]] || npm ci; } && npm run typecheck && npm test)
