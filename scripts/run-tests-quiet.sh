#!/usr/bin/env bash
# Quiet test runner for PrintFarmer
# Runs API (.NET) tests and (optionally) React/Vitest tests, capturing detailed logs
# and producing concise markdown summaries to avoid overwhelming the editor console.
#
# Usage:
#   scripts/run-tests-quiet.sh                        # run both API + frontend (if frontend present)
#   scripts/run-tests-quiet.sh api                    # only API tests
#   scripts/run-tests-quiet.sh frontend               # only frontend tests
#   scripts/run-tests-quiet.sh api --skip-docker      # skip tests containing SlicerServices / PrusaSlicerDockerIntegration
# Environment / Flags:
#   --skip-docker   : filters out known long-running docker-based slicer tests
#   FAST=1          : alias for --skip-docker (legacy convenience)
#
# Outputs written under test-logs/ with timestamped filenames.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOG_DIR="$ROOT_DIR/test-logs"
mkdir -p "$LOG_DIR"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
SUMMARY_MD="$LOG_DIR/test-summary-$TIMESTAMP.md"

run_api=true
run_frontend=true
skip_docker=false

POSITIONAL=()
for arg in "$@"; do
  case "$arg" in
    api) run_frontend=false ;;
    frontend) run_api=false ;;
    --skip-docker) skip_docker=true ;;
    *) POSITIONAL+=("$arg") ;;
  esac
done
if [[ "${FAST:-}" == "1" ]]; then
  skip_docker=true
fi
if [ ${#POSITIONAL[@]} -gt 0 ]; then
  set -- "${POSITIONAL[@]}"
else
  set --
fi

FILTER_ARG=""
if $skip_docker; then
  echo "[INFO] --skip-docker specified -> filtering out Category=Docker and Category=Slow tests";
  # Exclude multiple categories: (TestCategory!=Docker)&(TestCategory!=Slow)
  FILTER_ARG='--filter (TestCategory!=Docker)&(TestCategory!=Slow)'
fi

INTERRUPTED=false
trap 'INTERRUPTED=true' INT TERM

indent() { sed 's/^/    /'; }

# --- API TESTS ---
if $run_api; then
  API_STDOUT="$LOG_DIR/api-tests-$TIMESTAMP.stdout"
  API_TRX="$LOG_DIR/api-tests-$TIMESTAMP.trx"
  echo "[INFO] Running API tests (output -> $(basename "$API_STDOUT"), TRX -> $(basename "$API_TRX"))"
  API_STATUS=0
  # Run tests from solution root (ensures TRX path valid) - don't subshell so exit code propagates.
  pushd "$ROOT_DIR/src" >/dev/null
  RESULTS_DIR="$LOG_DIR/results-$TIMESTAMP"
  mkdir -p "$RESULTS_DIR"
  dotnet test ./farm-web.sln -c Debug ${FILTER_ARG:-} --results-directory "$RESULTS_DIR" -l "trx" --logger "console;verbosity=minimal" >"$API_STDOUT" 2>&1 || API_STATUS=$?
  # Find the newest TRX in results dir
  API_TRX_FOUND=$(ls -1t "$RESULTS_DIR"/*.trx 2>/dev/null | head -1 || true)
  if [[ -n "$API_TRX_FOUND" ]]; then
    cp "$API_TRX_FOUND" "$API_TRX" || true
  fi
  popd >/dev/null || true

  # Parse TRX if present else fallback to stdout heuristics.
  TOTAL=0; FAILED=0; PASSED=0; SKIPPED=0; DURATION_RAW=""; SLOW_FILE=""; START_TIME=""; FINISH_TIME=""; DURATION_HUMAN="";
  FAILED_LIST_FILE="$LOG_DIR/api-failed-tests-$TIMESTAMP.txt"
  if [[ -f "$API_TRX" ]]; then
  # TRX uses lowercase attributes (outcome=) inside UnitTestResult elements
  TOTAL=$(grep -c '<UnitTestResult' "$API_TRX" || true)
  FAILED=$(grep '<UnitTestResult' "$API_TRX" | grep -c 'outcome="Failed"' || true)
  PASSED=$(grep '<UnitTestResult' "$API_TRX" | grep -c 'outcome="Passed"' || true)
  SKIPPED=$(grep '<UnitTestResult' "$API_TRX" | grep -c 'outcome="Skipped"' || true)

    # Extract start/finish times from <Times ...> element and compute duration if possible
    TIMES_LINE=$(grep -m1 '<Times ' "$API_TRX" || true)
    if [[ -n "$TIMES_LINE" ]]; then
      START_TIME=$(echo "$TIMES_LINE" | sed -n 's/.*start="\([^"]*\)".*/\1/p')
      FINISH_TIME=$(echo "$TIMES_LINE" | sed -n 's/.*finish="\([^"]*\)".*/\1/p')
      if [[ -n "$START_TIME" && -n "$FINISH_TIME" ]]; then
        if command -v python3 >/dev/null 2>&1; then
          DURATION_SEC=$(python3 - <<PYEOF
import datetime, sys
def parse(ts):
    # Normalize potential trailing Z or timezone offset
    return datetime.datetime.fromisoformat(ts.replace('Z','+00:00'))
try:
    s=parse("$START_TIME")
    f=parse("$FINISH_TIME")
    delta=f-s
    secs=int(delta.total_seconds())
    h=secs//3600; m=(secs%3600)//60; s=secs%60
    print(f"{h:02d}:{m:02d}:{s:02d}")
except Exception:
    pass
PYEOF
          )
        elif command -v node >/dev/null 2>&1; then
          DURATION_SEC=$(node -e "const s=Date.parse('$START_TIME'); const f=Date.parse('$FINISH_TIME'); if(!isNaN(s)&&!isNaN(f)){const d=Math.round((f-s)/1000); const h=String(Math.floor(d/3600)).padStart(2,'0'); const m=String(Math.floor((d%3600)/60)).padStart(2,'0'); const sec=String(d%60).padStart(2,'0'); console.log(h+':'+m+':'+sec);} ")
        else
          DURATION_SEC=""
        fi
        DURATION_HUMAN="$DURATION_SEC"
      fi
    fi

    # Failed test names (restrict to UnitTestResult elements only)
  grep '<UnitTestResult' "$API_TRX" | grep 'outcome="Failed"' | sed -E 's/.*testName="([^"]+)".*/\1/' | sort -u > "$FAILED_LIST_FILE" || true

    # Extract top 10 slowest tests (by duration attribute inside UnitTestResult elements)
    SLOW_FILE="$LOG_DIR/api-slowest-tests-$TIMESTAMP.txt"
    grep -o '<UnitTestResult[^>]*>' "$API_TRX" | \
      sed -E 's/.*testName="([^"]+)"[^>]*duration="([0-9.:]+)".*/\2 \1/' | \
      awk '{print $0}' | sort -r | head -10 > "$SLOW_FILE" || true
  else
    echo "[WARN] TRX file not produced for API tests." > "$FAILED_LIST_FILE"
    # Heuristic: xUnit failure lines often contain 'Failed' preceded by optional spaces
    grep -E '^\s*Failed ' "$API_STDOUT" | sed -E 's/^\s*Failed (.+) \[[0-9]+ .*\].*/\1/' | sort -u >> "$FAILED_LIST_FILE" || true
    FAILED=$(grep -E '^\s*Failed ' "$API_STDOUT" | wc -l | tr -d ' ' || echo 0)
  if [[ "$TOTAL" == "0" && "$FAILED" =~ ^[0-9]+$ && $FAILED -gt 0 ]]; then
      TOTAL=$FAILED
    fi
  fi

  {
    echo "# Test Summary ($TIMESTAMP)"; echo
    echo "## API Tests"; echo
    echo "| Metric | Value |"
    echo "|--------|-------|"
    echo "| Total | $TOTAL |"
    echo "| Passed | $PASSED |"
    echo "| Failed | $FAILED |"
    echo "| Skipped | $SKIPPED |"
    PASS_RATE=""
    if [[ "$TOTAL" =~ ^[0-9]+$ && "$TOTAL" -gt 0 ]]; then
      if command -v python3 >/dev/null 2>&1; then
        PASS_RATE=$(python3 - <<PYEOF
total=$TOTAL
passed=$PASSED
print(f"{(passed/total)*100:.2f}%")
PYEOF
        )
      else
        PASS_RATE=$(awk -v p=$PASSED -v t=$TOTAL 'BEGIN{ if(t>0){ printf("%.2f%%", (p/t)*100) } }')
      fi
    fi
    echo "| Exit Code | $API_STATUS |"
    [[ -n "$PASS_RATE" ]] && echo "| Pass Rate | $PASS_RATE |"
    if [[ -n "$DURATION_HUMAN" ]]; then
      echo "| Duration | $DURATION_HUMAN |"
    elif [[ -n "$START_TIME" && -n "$FINISH_TIME" ]]; then
      echo "| Start | $START_TIME |"
      echo "| Finish | $FINISH_TIME |"
    fi
    echo
    if $skip_docker; then
      echo "_Note: Filter active (excluded tests with Category=Docker or Category=Slow). Reported 'Total' reflects executed tests only if the test platform applied the filter before discovery; currently raw TRX count may still include all discovered tests._"; echo
    fi
    if [[ "$FAILED" =~ ^[0-9]+$ ]] && [ "$FAILED" -gt 0 ]; then
      echo "### Failed Tests ($FAILED)"; echo
      # Group by class prefix (split at last dot before test method argument parentheses)
      awk 'NF{print}' "$FAILED_LIST_FILE" | while read -r line; do
        echo "$line"
      done | awk '{
        # Extract class (everything up to last dot before first parenthesis or end)
        name=$0
        method=name
        class=name
        if(match(name,/\(/)) { base=substr(name,1,RSTART-1); } else { base=name }
        if(match(base,/\.[^.]+$/)) { class=substr(base,1,RSTART-1); method=substr(base,RSTART+1) } else { class=base; method=base }
        print class"\t"name
      }' | sort | awk -F'\t' 'BEGIN{current=""} {
        cls=$1; full=$2;
        if(cls!=current){ if(current!=""){ print "" } print "- "cls":"; current=cls }
        print "  - "full
      }' 
      echo
    fi
    if [[ -n "$SLOW_FILE" && -f "$SLOW_FILE" ]]; then
      echo "### Slowest Tests (Top 10)"; echo
      while IFS= read -r line; do
        testDur=${line%% *}
        testName=${line#* }
        [[ -n "$testName" ]] && echo "- $testDur $testName"
      done < "$SLOW_FILE"
      echo
    fi
  } > "$SUMMARY_MD.tmp"
else
  echo "# Test Summary ($TIMESTAMP)" > "$SUMMARY_MD.tmp"
fi

# --- FRONTEND TESTS ---
if $run_frontend; then
  REACT_APP_DIR="$ROOT_DIR/src/Web/ReactApp"
  if [[ -d "$REACT_APP_DIR" ]]; then
    REACT_STDOUT="$LOG_DIR/react-tests-$TIMESTAMP.stdout"
    REACT_JSON="$LOG_DIR/react-tests-$TIMESTAMP.json"
    REACT_STATUS=0
    echo "[INFO] Running frontend (Vitest) tests (output -> $(basename "$REACT_STDOUT"))"
    (
      cd "$REACT_APP_DIR"
      # Run in non-watch mode; prefer json reporter for parsing if available.
      npx vitest run --reporter=json > "$REACT_JSON" 2> "$REACT_STDOUT" || REACT_STATUS=$?
    ) || REACT_STATUS=$?

    # Parse JSON reporter (fallback if jq absent: use grep)
    if [[ -s "$REACT_JSON" ]]; then
      if command -v jq >/dev/null 2>&1; then
        VT_TOTAL=$(jq -r 'try .numTotalTests // empty' "$REACT_JSON" 2>/dev/null || echo 0)
        VT_PASSED=$(jq -r 'try .numPassedTests // empty' "$REACT_JSON" 2>/dev/null || echo 0)
        VT_FAILED=$(jq -r 'try .numFailedTests // empty' "$REACT_JSON" 2>/dev/null || echo 0)
        VT_SKIPPED=$(jq -r 'try .numSkippedTests // empty' "$REACT_JSON" 2>/dev/null || echo 0)
      else
        VT_TOTAL=$(grep -o '"numTotalTests"[: ]*[0-9]*' "$REACT_JSON" | head -1 | grep -o '[0-9]*' || echo 0)
        VT_PASSED=$(grep -o '"numPassedTests"[: ]*[0-9]*' "$REACT_JSON" | head -1 | grep -o '[0-9]*' || echo 0)
        VT_FAILED=$(grep -o '"numFailedTests"[: ]*[0-9]*' "$REACT_JSON" | head -1 | grep -o '[0-9]*' || echo 0)
        VT_SKIPPED=$(grep -o '"numSkippedTests"[: ]*[0-9]*' "$REACT_JSON" | head -1 | grep -o '[0-9]*' || echo 0)
      fi
      {
        echo "## Frontend (Vitest)"; echo
        echo "| Metric | Value |"
        echo "|--------|-------|"
        echo "| Total | $VT_TOTAL |"
        echo "| Passed | $VT_PASSED |"
        echo "| Failed | $VT_FAILED |"
        echo "| Skipped | $VT_SKIPPED |"
        echo "| Exit Code | $REACT_STATUS |"
        echo
      } >> "$SUMMARY_MD.tmp"
    else
      {
        echo "## Frontend (Vitest)"; echo
        echo "No JSON reporter output produced (possible test infra issue)."; echo
      } >> "$SUMMARY_MD.tmp"
    fi
  else
    echo "## Frontend (Vitest)" >> "$SUMMARY_MD.tmp"
    echo "ReactApp directory not found; skipping." >> "$SUMMARY_MD.tmp"
  fi
fi

if $INTERRUPTED; then
  echo "### Run Status" >> "$SUMMARY_MD.tmp"
  echo "Run was interrupted by signal (partial results)." >> "$SUMMARY_MD.tmp"
  echo >> "$SUMMARY_MD.tmp"
fi

mv "$SUMMARY_MD.tmp" "$SUMMARY_MD"

echo "[INFO] Summary written: $SUMMARY_MD"
echo "[INFO] Done."