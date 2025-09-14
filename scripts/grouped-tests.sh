#!/usr/bin/env bash
set -euo pipefail

# Debug: enable with DEBUG=1 environment variable
if [[ "${DEBUG:-0}" == "1" ]]; then
  set -x
fi

# Purpose: Run xUnit test groups sequentially to isolate long-running or hanging groups.
# Groups are determined by xUnit Trait("Category", <value>) usage.
# Existing categories in this repo: Docker, DbHeavy, Slow. Others are treated as Fast (default).
#
# Usage examples:
#   scripts/grouped-tests.sh                # run all groups sequentially
#   INCLUDE=Fast,DbHeavy scripts/grouped-tests.sh   # run only selected groups
#   EXCLUDE=Docker scripts/grouped-tests.sh         # skip docker group
#   MAX_THREADS=1 scripts/grouped-tests.sh          # override parallel threads
#
# Output: Per-group timing + overall JSON summary at the end.
#
# Requires: dotnet SDK in PATH

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_PROJ="${ROOT_DIR}/src/tests/Farm.Web.Api.Tests/Farm.Web.Api.Tests.csproj"
RESULTS_DIR="${ROOT_DIR}/test-logs/grouped"; mkdir -p "$RESULTS_DIR"

MAX_THREADS="${MAX_THREADS:-4}"
INCLUDE="${INCLUDE:-}" # comma separated list of groups to include
EXCLUDE="${EXCLUDE:-}" # comma separated list of groups to exclude

IFS=',' read -r -a INCLUDE_ARR <<< "${INCLUDE}"
IFS=',' read -r -a EXCLUDE_ARR <<< "${EXCLUDE}"

GROUPS=(Fast DbHeavy Slow Docker)
[[ "${DEBUG:-0}" == "1" ]] && echo "[DEBUG] GROUPS: ${GROUPS[*]}"

should_run() {
  local g="$1"
  if [[ -n "$INCLUDE" ]]; then
    local found=0
    for inc in "${INCLUDE_ARR[@]}"; do [[ "$inc" == "$g" ]] && found=1; done
    [[ $found -eq 0 ]] && return 1
  fi
  if [[ -n "$EXCLUDE" ]]; then
    for exc in "${EXCLUDE_ARR[@]}"; do [[ "$exc" == "$g" ]] && return 1; done
  fi
  return 0
}

summary_json="{\n  \"groups\": [\n"
first_group=1
TOTAL_START=$(date +%s)

run_group() {
  local group="$1"
  local filter
  [[ "${DEBUG:-0}" == "1" ]] && echo "[DEBUG] Enter run_group with group='$group'"
  case "$group" in
    Fast)
      filter='Category!=Docker&Category!=DbHeavy&Category!=Slow'
      ;;
    DbHeavy)
      filter='Category=DbHeavy'
      ;;
    Slow)
      filter='Category=Slow'
      ;;
    Docker)
      filter='Category=Docker'
      ;;
    *)
      echo "Unknown group: $group" >&2; return 1;;
  esac
  [[ "${DEBUG:-0}" == "1" ]] && echo "[DEBUG] Filter resolved to '$filter' for group '$group'"
  local start end dur logfile trxfile
  logfile="$RESULTS_DIR/${group}.log"
  trxfile="$RESULTS_DIR/${group}.trx"
  echo "===== Running group: $group (filter: $filter) =====" | tee "$logfile"
  start=$(date +%s)
  if ! dotnet test "$TEST_PROJ" \
      --no-build \
      --filter "$filter" \
      -p:ParallelizeTestCollections=true \
      -p:MaxParallelThreads=$MAX_THREADS \
      --logger "trx;LogFileName=$(basename "$trxfile")" \
      --logger "console;verbosity=normal" | tee -a "$logfile"; then
    status="fail"
  else
    status="pass"
  fi
  end=$(date +%s); dur=$(( end - start ))
  echo "Group $group duration: ${dur}s (status=$status)" | tee -a "$logfile"
  [[ $first_group -eq 0 ]] && summary_json+="  ," || first_group=0
  summary_json+=$'    {"name":"'$group'","seconds":'$dur',"status":"'$status'"}'$'\n'
}

for g in "${GROUPS[@]}"; do
  if should_run "$g"; then
    run_group "$g"
  else
    echo "Skipping group $g" >&2
  fi
done

TOTAL_END=$(date +%s)
TOTAL_DUR=$(( TOTAL_END - TOTAL_START ))
summary_json+=$'  ],\n  "totalSeconds": '$TOTAL_DUR'\n}'

echo -e "\n===== Summary =====\n$summary_json" | tee "$RESULTS_DIR/summary.json"
