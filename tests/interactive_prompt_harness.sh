#!/usr/bin/env bash
# Minimal harness: extract only the prompt functions from deploy-docker.sh and source them.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

TMPFILE="/tmp/prompt_funcs.$$"
rm -f "$TMPFILE"

# Helper to extract a function by name from deploy-docker.sh (handles nested braces)
extract_function() {
  local name="$1"; local infile="$REPO_ROOT/scripts/deploy-docker.sh"
  local in=0; local depth=0
  while IFS= read -r line || [ -n "$line" ]; do
    if [ $in -eq 0 ]; then
      if printf '%s' "$line" | grep -qE "^${name}\(\) \{"; then
        in=1; depth=0; echo "$line" >> "$TMPFILE"; continue
      fi
    else
      echo "$line" >> "$TMPFILE"
      # count { and } (simple heuristic)
      open_count=$(printf '%s' "$line" | awk -F"{" '{print NF-1}')
      close_count=$(printf '%s' "$line" | awk -F"}" '{print NF-1}')
      depth=$((depth + open_count - close_count))
      if [ $depth -lt 0 ]; then
        # function likely ended
        break
      fi
    fi
  done < "$infile"
}

extract_function prompt_with_default
extract_function prompt_yes_no

# Provide minimal dependencies used by functions
cat >> "$TMPFILE" <<'EOF'
NON_INTERACTIVE=false
YELLOW=""
BLUE=""
NC=""
EOF

# Source the extracted functions
. "$TMPFILE"

# Test prompt_yes_no picks up existing variable
export INCLUDE_MONITORING="true"
# Simulate seeding behavior done by configure_additional(): copy INCLUDE_MONITORING -> INCLUDE_MONITORING_CHOICE
if [[ "${INCLUDE_MONITORING}" =~ ^(true|yes|1)$ ]]; then
  INCLUDE_MONITORING_CHOICE="yes"
else
  INCLUDE_MONITORING_CHOICE="no"
fi
NON_INTERACTIVE=true
prompt_yes_no "Enable monitoring stack (Prometheus, Grafana)?" "no" "INCLUDE_MONITORING_CHOICE"
if [ "$INCLUDE_MONITORING_CHOICE" != "yes" ]; then
  echo "[FAIL] prompt_yes_no did not use existing INCLUDE_MONITORING_CHOICE as default"
  rm -f "$TMPFILE"
  exit 1
fi

# Test prompt_with_default defaulting behavior in non-interactive mode
POSTGRES_DB_CHOICE=""
NON_INTERACTIVE=true
prompt_with_default "Postgres DB name:" "printfarmer" "POSTGRES_DB_CHOICE"
if [ "${POSTGRES_DB_CHOICE:-}" != "printfarmer" ]; then
  echo "[FAIL] prompt_with_default did not set default in non-interactive mode (expected 'printfarmer', got '${POSTGRES_DB_CHOICE:-}')"
  rm -f "$TMPFILE"
  exit 1
fi

rm -f "$TMPFILE"
echo "[PASS] interactive prompt harness checks passed"
