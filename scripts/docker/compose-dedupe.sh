#!/usr/bin/env bash
# Wrapper to call the Python-based compose dedupe tool. Keeps a stable shell entrypoint
# so the compose generator doesn't need to change.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PY="$SCRIPT_DIR/compose-dedupe.py"

if [[ ! -x "$PY" ]]; then
  # Try to create the Python file if it exists as non-executable (helpful in some checkouts)
  if [[ -f "$PY" ]]; then
    chmod +x "$PY" || true
  else
    echo "compose-dedupe helper not found: $PY" >&2
    exit 0
  fi
fi

exec python3 "$PY" "$@"
