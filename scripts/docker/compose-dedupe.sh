#!/usr/bin/env bash
# Wrapper to call the Python-based compose dedupe tool. Keeps a stable shell entrypoint
# so the compose generator doesn't need to change.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PY="$SCRIPT_DIR/compose-dedupe.py"
PYTHON_BIN="${PYTHON_BIN:-}"

if [[ ! -x "$PY" ]]; then
  # Try to create the Python file if it exists as non-executable (helpful in some checkouts)
  if [[ -f "$PY" ]]; then
    chmod +x "$PY" || true
  else
    echo "compose-dedupe helper not found: $PY" >&2
    exit 0
  fi
fi

for candidate in "$PYTHON_BIN" python3 python; do
  [[ -z "$candidate" ]] && continue
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c "import sys; raise SystemExit(0 if sys.version_info.major == 3 else 1)" >/dev/null 2>&1; then
    exec "$candidate" "$PY" "$@"
  fi
done

echo "Python 3 is required to run compose-dedupe.py" >&2
exit 1
