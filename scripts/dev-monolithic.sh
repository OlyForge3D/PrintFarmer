#!/usr/bin/env bash
# Monolithic dev launcher (React + API) with port freeing & optional foreground.
# Default behavior: starts both processes in background and exits (non-blocking).

set -euo pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SRC_DIR="$ROOT_DIR/src"
API_DIR="$SRC_DIR/api"
REACT_DIR="$SRC_DIR/Web/ReactApp"

BACKGROUND_MODE=${BACKGROUND_MODE:-1}   # Set to 0 to run in foreground (legacy wait behavior)
LOG_DIR=${LOG_DIR:-/tmp}
PID_DIR=${PID_DIR:-/tmp}
API_LOG="$LOG_DIR/printfarmer-api.log"
VITE_LOG="$LOG_DIR/printfarmer-react.log"
META_PID_FILE="$PID_DIR/printfarmer-monolith.pids"

export DEPLOYMENT_MODE=monolithic
export ASPNETCORE_ENVIRONMENT=Development
export ALLOWED_ORIGINS=${ALLOWED_ORIGINS:-"http://localhost:3000"}
export ASPNETCORE_URLS=${ASPNETCORE_URLS:-"http://localhost:5245"}
export SPA_DEV_URL=${SPA_DEV_URL:-"http://localhost:3000"}

API_PORT=${ASPNETCORE_URLS##*:}  # naive parse of last colon section (http://host:PORT)
STD_REACT_PORT=3000

free_port() {
  local port="$1"
  local pids
  pids=$(lsof -ti:"${port}" -sTCP:LISTEN 2>/dev/null || true)
  if [[ -n "$pids" ]]; then
    echo "[port-free] Reclaiming port ${port} (PIDs: $pids)" >&2
    # shellcheck disable=SC2086
    kill -9 $pids 2>/dev/null || true
    sleep 0.5
  fi
}

echo "== PrintFarmer Monolithic Dev Start =="
echo "API URL: ${ASPNETCORE_URLS} (port ${API_PORT})"
echo "SPA Dev URL (expected): ${SPA_DEV_URL}"

echo "Freeing standard ports (API ${API_PORT}, React ${STD_REACT_PORT}) if occupied..."
free_port "${API_PORT}"
free_port "${STD_REACT_PORT}"

mkdir -p "$LOG_DIR" "$PID_DIR"
rm -f "$API_LOG" "$VITE_LOG" "$META_PID_FILE"

echo "Starting React (Vite) dev server... (logs => $VITE_LOG)"
(
  cd "$REACT_DIR"
  npm install >/dev/null 2>&1 || true
  # Run dev; Vite may pick alternate port if 3000 busy; we capture first matching 'Local:' line
  npm run dev >>"$VITE_LOG" 2>&1 &
  echo $! > "$PID_DIR/printfarmer-vite.pid"
) &
VITE_LAUNCHER_PID=$!

echo "Starting API server... (logs => $API_LOG)"
(
  cd "$SRC_DIR"
  dotnet run --project api/Farm.Web.Api.csproj >>"$API_LOG" 2>&1 &
  echo $! > "$PID_DIR/printfarmer-api.pid"
) &
API_LAUNCHER_PID=$!

sleep 2

if [[ -f "$PID_DIR/printfarmer-vite.pid" ]]; then
  VITE_PID=$(cat "$PID_DIR/printfarmer-vite.pid" 2>/dev/null || echo '?')
fi
if [[ -f "$PID_DIR/printfarmer-api.pid" ]]; then
  API_PID=$(cat "$PID_DIR/printfarmer-api.pid" 2>/dev/null || echo '?')
fi

echo "React PID: ${VITE_PID:-?}"
echo "API   PID: ${API_PID:-?}"
echo "Vite Log: $VITE_LOG"
echo "API  Log: $API_LOG"
echo "PID meta: $META_PID_FILE"
echo "Vite will print its chosen Local URL shortly (tail -f $VITE_LOG)"

printf 'API health check once ready: curl -s %s/healthz\n' "${ASPNETCORE_URLS}"
printf 'When SPA dev server ready, proxied root: curl -I %s/\n' "${ASPNETCORE_URLS}"

echo "API_PID=${API_PID:-}" > "$META_PID_FILE"
echo "VITE_PID=${VITE_PID:-}" >> "$META_PID_FILE"
echo "ASPNETCORE_URLS=${ASPNETCORE_URLS}" >> "$META_PID_FILE"
echo "SPA_DEV_URL=${SPA_DEV_URL}" >> "$META_PID_FILE"

cleanup() {
  echo "Stopping monolith (API:$API_PID Vite:$VITE_PID)" >&2
  kill "$API_PID" "$VITE_PID" 2>/dev/null || true
}
trap cleanup INT TERM

if [[ $BACKGROUND_MODE -eq 0 ]]; then
  echo "Foreground mode: waiting (Ctrl+C to stop)"; wait "$API_PID" "$VITE_PID" 2>/dev/null || true
else
  echo "Background mode enabled: script exiting now; processes keep running."; exit 0
fi
