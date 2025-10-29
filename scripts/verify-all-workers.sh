#!/usr/bin/env bash
set -euo pipefail

# Orchestrator to verify all slicer worker containers (Prusa & Orca) using existing per-worker scripts.
#
# Usage:
#   scripts/verify-all-workers.sh [options]
#
# Options:
#   --mode MODE           Global mode for all workers (allow-stub | require-real) (default: allow-stub)
#   --prusa-mode MODE     Override mode just for prusaslicer-worker
#   --orca-mode MODE      Override mode just for orcaslicer-worker
#   --workers LIST        Comma-separated subset (default: prusa,orca)
#   --fail-fast           Stop on first failure (default: continue)
#   --skip-missing        Skip workers whose image is missing locally instead of failing
#   --summary-only        Suppress per-worker success chatter (errors still shown)
#   --quiet               Only final summary (implies --summary-only)
#   --json                Emit machine-readable JSON summary to stdout (in addition to logs)
#   --output FILE         Write JSON summary to FILE (implies --json)
#   --no-detect           Disable automatic local image detection; rely only on env/defaults
#   --help                Show this help
#
# Environment Variable Overrides (per worker scripts already honor these):
#   PRUSA_IMAGE, PRUSA_CONTAINER, PRUSA_MODE
#   ORCA_IMAGE,  ORCA_CONTAINER,  ORCA_MODE
#
# Additional Orchestrator Environment Variables:
#   ALL_WORKERS           Comma list overriding default set (same as --workers)
#
# Exit Codes:
#   0  All requested worker verifications succeeded / were skipped (with --skip-missing)
#   1  At least one worker failed (or missing without --skip-missing)
#   2  Docker missing
#   64+ Internal orchestrator argument errors
#
# Notes:
#   - Per-worker detailed semantics / exit codes documented in each verify-*-worker.sh script.
#   - This orchestrator reduces them to pass/fail (non-zero) for aggregation.
#   - When a worker is skipped (image missing + --skip-missing), it is reported as SKIPPED.

show_help() { grep '^#' "$0" | sed 's/^# \{0,1\}//'; }

log() { printf '[verify-all] %s\n' "$*"; }
err() { log "ERROR: $*" >&2; }

if [[ ${1-} == '--help' || ${1-} == '-h' ]]; then
  show_help; exit 0;
fi

if ! command -v docker >/dev/null 2>&1; then
  err "Docker CLI not found"; exit 2
fi

GLOBAL_MODE=allow-stub
ORCA_MODE=""
WORKERS=${ALL_WORKERS:-"orca"}
FAIL_FAST=false
SKIP_MISSING=false
SUMMARY_ONLY=false
QUIET=false
JSON_MODE=false
OUTPUT_FILE=""
DETECT_IMAGES=true

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) GLOBAL_MODE="${2:?mode requires value}"; shift 2;;
    --orca-mode) ORCA_MODE="${2:?orca-mode requires value}"; shift 2;;
    --workers) WORKERS="${2:?workers requires value}"; shift 2;;
    --fail-fast) FAIL_FAST=true; shift;;
    --skip-missing) SKIP_MISSING=true; shift;;
    --summary-only) SUMMARY_ONLY=true; shift;;
  --quiet) QUIET=true; SUMMARY_ONLY=true; shift;;
  --json) JSON_MODE=true; shift;;
  --output) OUTPUT_FILE="${2:?--output requires file path}"; JSON_MODE=true; shift 2;;
  --no-detect) DETECT_IMAGES=false; shift;;
    --help|-h) show_help; exit 0;;
    *) err "Unknown argument: $1"; exit 64;;
  esac
done

# Normalize workers list
IFS=',' read -r -a WORKER_LIST <<< "${WORKERS}"

declare -A RESULTS
declare -A EXITCODES
declare -A MODES
declare -A IMAGES
declare -A REASONS

# Cache docker images list (repository:tag)
DOCKER_IMAGES=$(docker images --format '{{.Repository}}:{{.Tag}}')

find_candidate_image() {
  local worker="$1"; local candidate=""; local patterns=()
  case "$worker" in
    orca)  patterns=("orcaslicer-worker:latest" "orcaslicer-worker-test:latest" "orcaslicer-worker-dev:latest" "orcaslicer-worker" "orcaslicer-worker-test" "orcaslicer-worker-dev");;
    *) return 1;;
  esac
  for p in "${patterns[@]}"; do
    if [[ "$p" == *:* ]]; then
      # Pattern already includes tag; look for exact repository:tag line
      if echo "$DOCKER_IMAGES" | grep -Fxq "$p"; then
        echo "$p"; return 0
      fi
    else
      # No tag: pick first matching repo with any tag
      candidate=$(echo "$DOCKER_IMAGES" | awk -F: -v repo="$p" '$1==repo {print $0; exit}')
      if [[ -n "$candidate" ]]; then
        echo "$candidate"; return 0
      fi
    fi
  done
  return 1
}

run_worker() {
  local worker="$1"; shift
  local mode_override="$1"; shift
  local script
  local mode_to_use
  local resolved_image=""
  case "$worker" in
    orca)  script="$(dirname "$0")/verify-orcaslicer-worker.sh"; mode_to_use="${mode_override:-${GLOBAL_MODE}}";;
    *) err "Unsupported worker identifier: $worker"; RESULTS["$worker"]=UNKNOWN; EXITCODES["$worker"]=65; return 65;;
  esac

  # Determine image from env or script default (by inspecting help header if needed)
  local image_env_var
  case "$worker" in
    orca)  image_env_var="ORCA_IMAGE";;
    *) image_env_var="";;
  esac
  local image="${!image_env_var:-}" # may be empty; script has its own default

  # Auto-detect image if not explicitly provided
  if $DETECT_IMAGES && [[ -z "$image" ]]; then
    if candidate=$(find_candidate_image "$worker" 2>/dev/null); then
      image="$candidate"
      $QUIET || log "Detected image for $worker: $image"
    fi
  fi

  # Validate explicit or detected image if set
  if [[ -n "$image" ]]; then
    if ! docker image inspect "$image" >/dev/null 2>&1; then
      if $SKIP_MISSING; then
        log "Skipping $worker (image '$image' missing)"; RESULTS["$worker"]=SKIPPED; EXITCODES["$worker"]=0; IMAGES["$worker"]="$image"; MODES["$worker"]="$mode_to_use"; REASONS["$worker"]="image-missing"; return 0
      else
        err "Image '$image' for $worker missing"; RESULTS["$worker"]=MISSING; EXITCODES["$worker"]=1; IMAGES["$worker"]="$image"; MODES["$worker"]="$mode_to_use"; REASONS["$worker"]="image-missing"; return 1
      fi
    fi
  fi

  if ! [[ -x "$script" ]]; then
    err "Script not executable: $script"; RESULTS["$worker"]=ERROR; EXITCODES["$worker"]=66; return 66
  fi

  $QUIET || log "Verifying $worker (mode=$mode_to_use${image:+ image=$image})..."
  set +e
  if [[ -n "$image" ]]; then
    "$script" "$mode_to_use" "$image" >/tmp/verify-${worker}.log 2>&1
  else
    "$script" "$mode_to_use" >/tmp/verify-${worker}.log 2>&1
  fi
  local rc=$?
  set -e

  if [ $rc -eq 0 ]; then
  RESULTS["$worker"]=OK
  EXITCODES["$worker"]=0
  MODES["$worker"]="$mode_to_use"
  IMAGES["$worker"]="${image:-default}"
  REASONS["$worker"]=
    if ! $SUMMARY_ONLY; then
      $QUIET || log "$worker: SUCCESS"
    fi
    return 0
  else
  RESULTS["$worker"]=FAIL
  EXITCODES["$worker"]=$rc
  MODES["$worker"]="$mode_to_use"
  IMAGES["$worker"]="${image:-default}"
  # Extract first ERROR line as reason if present
  local reason_line
  reason_line=$(grep -m1 'ERROR:' "/tmp/verify-${worker}.log" | sed 's/.*ERROR: //') || true
  REASONS["$worker"]="${reason_line:-exit-$rc}"
    err "$worker: FAILED (exit $rc). Showing tail of log:"
    sed -n '1,200p' "/tmp/verify-${worker}.log" | sed "s/^/[verify-all][$worker] /" >&2 || true
    return $rc
  fi
}

overall_rc=0

for w in "${WORKER_LIST[@]}"; do
  rc=0
  case "$w" in
    orca)  run_worker orca  "$ORCA_MODE"  || rc=$?;;
    *) err "Unknown or unsupported worker '$w' (skipping)"; RESULTS["$w"]=UNKNOWN; EXITCODES["$w"]=67; rc=67;;
  esac
  if [ $rc -ne 0 ]; then overall_rc=$rc; fi
  if $FAIL_FAST && [ $overall_rc -ne 0 ]; then break; fi
done

# Summary
if ! $QUIET; then
  log "--- Verification Summary ---"
  printf '%-10s %-8s %-8s\n' 'Worker' 'Result' 'Code'
  printf '%-10s %-8s %-8s\n' '------' '------' '----'
  for w in "${WORKER_LIST[@]}"; do
    printf '%-10s %-8s %-8s\n' "$w" "${RESULTS[$w]:-N/A}" "${EXITCODES[$w]:-?}"
  done
fi

if [ $overall_rc -eq 0 ]; then
  log "All verifications succeeded (or skipped)."
else
  err "One or more verifications failed (overall exit $overall_rc)."
fi

# JSON output mode
if $JSON_MODE; then
  # Build JSON manually (limited characters expected). Simple escaping for quotes/backslashes in reasons.
  json_escape() { echo -n "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
  OUTPUT='{'"\n"'  "schemaVersion": 1,'"\n"'  "overall": {"status": '"\"$([ $overall_rc -eq 0 ] && echo ok || echo failed)\""', "exitCode": '$overall_rc' },'"\n"'  "workers": ['
  first=true
  for w in "${WORKER_LIST[@]}"; do
    result="${RESULTS[$w]:-N/A}"; code="${EXITCODES[$w]:-?}"; mode="${MODES[$w]:-unknown}"; image="${IMAGES[$w]:-unknown}"; reason="${REASONS[$w]:-}"
    [ -n "$reason" ] || reason="null"; [ "$reason" != "null" ] && reason="\"$(json_escape "$reason")\""
    $first || OUTPUT+=','
    first=false
    OUTPUT+=$'\n    {"name": "'$w'", "result": "'$result'", "exitCode": '$code', "mode": "'$mode'", "image": "'$(json_escape "$image")'", "reason": '$reason' }'
  done
  OUTPUT+=$'\n  ]\n}'
  echo "$OUTPUT"
  if [[ -n "$OUTPUT_FILE" ]]; then
    printf '%s
' "$OUTPUT" > "$OUTPUT_FILE"
    $QUIET || log "Wrote JSON output to $OUTPUT_FILE"
  fi
fi

exit $overall_rc
