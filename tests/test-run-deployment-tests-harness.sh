#!/bin/bash

# ============================================================================
# test-run-deployment-tests-harness.sh
#
# Direct regression for tests/run-deployment-tests.sh proving that after the
# first downstream suite returns success, the orchestrator continues into
# every subsequent suite instead of aborting at the pass counter.
#
# The historical bug: ((VAR++)) under `set -euo pipefail`. When the counter
# is 0 the post-increment expression evaluates to 0 and returns exit code 1,
# tripping errexit and aborting the orchestrator after the very first
# successful sub-suite. The safe form is VAR=$((VAR + 1)).
#
# This test uses mock sub-suite scripts so it is fast, does not depend on
# Docker/Compose, and isolates the harness counter behavior from real test
# content or infrastructure availability.
# ============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HARNESS="$SCRIPT_DIR/run-deployment-tests.sh"

RED=$'\033[0;31m'
GREEN=$'\033[0;32m'
NC=$'\033[0m'

pass() { printf '%s[PASS]%s %s\n' "$GREEN" "$NC" "$*"; }

fail() {
    printf '%s[FAIL]%s %s\n' "$RED" "$NC" "$*"
    exit 1
}

if [[ ! -f "$HARNESS" ]]; then
    fail "Harness script not found: $HARNESS"
fi

# ----------------------------------------------------------------------------
# Static regression: no unsafe ((VAR++)) / ((VAR--)) counters
# ----------------------------------------------------------------------------

unsafe=$(grep -nE '^[[:space:]]*\(\([A-Za-z_][A-Za-z0-9_]*(\+\+|--)\)\)' "$HARNESS" || true)
if [[ -n "$unsafe" ]]; then
    echo "Unsafe arithmetic counters found under set -e in $HARNESS:"
    echo "$unsafe"
    fail "run-deployment-tests.sh must not use ((VAR++))/((VAR--)); they abort under set -e when VAR=0. Use VAR=\$((VAR + 1))."
fi
pass "Static: no ((VAR++))/((VAR--)) counters in run-deployment-tests.sh"

# ----------------------------------------------------------------------------
# Dynamic regression: mock every downstream suite and verify the orchestrator
# invokes each one. Proves the pass counter increment does not abort the run.
# ----------------------------------------------------------------------------

TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

mock_tests_dir="$TMP_ROOT/tests"
mkdir -p "$mock_tests_dir"

# Copy the harness into the mock tests dir so its SCRIPT_DIR resolves to the
# mocks. The dependency check inside the harness looks for
# test-compose-generator.sh and test-deploy-docker.sh in SCRIPT_DIR, so the
# mocks below satisfy that too.
cp "$HARNESS" "$mock_tests_dir/run-deployment-tests.sh"
chmod +x "$mock_tests_dir/run-deployment-tests.sh"

marker_dir="$TMP_ROOT/markers"
mkdir -p "$marker_dir"

# The five sub-suite scripts referenced by run_full_tests. Order matters:
# the historical bug aborted after the first one succeeded, so seeing every
# marker present proves the orchestrator kept going.
suites=(
    test-compose-generator.sh
    test-deploy-docker.sh
    test-config-persistence.sh
    test-integration.sh
    test-user-scenario-complete.sh
)

for suite in "${suites[@]}"; do
    marker_path="$marker_dir/${suite%.sh}.marker"
    cat > "$mock_tests_dir/$suite" <<EOF
#!/bin/bash
# Mock sub-suite for harness regression: always succeeds and drops a marker.
: > "$marker_path"
exit 0
EOF
    chmod +x "$mock_tests_dir/$suite"
done

harness_out="$TMP_ROOT/harness.out"
if ! bash "$mock_tests_dir/run-deployment-tests.sh" --verbose >"$harness_out" 2>&1; then
    echo "Harness exit non-zero with mocked (always-passing) sub-suites. Output:"
    cat "$harness_out"
    fail "Orchestrator aborted with mocked passing sub-suites; likely a set -e counter regression."
fi
pass "Dynamic: orchestrator exited 0 with mocked passing sub-suites"

missing=()
for suite in "${suites[@]}"; do
    if [[ ! -f "$marker_dir/${suite%.sh}.marker" ]]; then
        missing+=("$suite")
    fi
done

if (( ${#missing[@]} > 0 )); then
    echo "Missing suite markers: ${missing[*]}"
    echo "Full harness output:"
    cat "$harness_out"
    fail "Orchestrator aborted before invoking all sub-suites (missing: ${missing[*]})."
fi
pass "Dynamic: all ${#suites[@]} downstream sub-suites were invoked"

# Strip ANSI color codes so text assertions are stable across environments.
plain_out=$(sed -r $'s/\x1b\\[[0-9;]*[A-Za-z]//g' "$harness_out")

if ! echo "$plain_out" | grep -q "Total Tests Run:"; then
    echo "$plain_out"
    fail "Summary block missing from harness output; print_summary was not reached."
fi
pass "Dynamic: summary block emitted (print_summary reached)"

if ! echo "$plain_out" | grep -q "ALL TESTS PASSED"; then
    echo "$plain_out"
    fail "Success banner missing; orchestrator did not conclude cleanly."
fi
pass "Dynamic: success banner emitted"

# Sanity: with 5 mocked suites, the summary must report exactly 5 passed,
# 5 total run, 0 failed. This proves the counters preserved their values.
if ! echo "$plain_out" | grep -Eq "Total Tests Run:[[:space:]]+5( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Total Tests Run: 5."
fi
pass "Dynamic: summary reports 5 total tests run"

if ! echo "$plain_out" | grep -Eq "Passed:[[:space:]]+5( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Passed: 5."
fi
pass "Dynamic: summary reports 5 passed"

if ! echo "$plain_out" | grep -Eq "Failed:[[:space:]]+0( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Failed: 0."
fi
pass "Dynamic: summary reports 0 failed"

# ----------------------------------------------------------------------------
# Dynamic regression #2: mixed pass/fail. The orchestrator must run all five
# suites even when the first one fails. Historical variant of the same
# set-e harness defect: run_test_suite returning 1 tripped errexit in
# run_full_tests and skipped the remaining suites. FAILED_TESTS and
# print_summary already carry the pass/fail signal, so suppressing the
# caller-side abort is safe.
# ----------------------------------------------------------------------------

marker_dir2="$TMP_ROOT/markers2"
mkdir -p "$marker_dir2"

# Suite #1 drops its marker then exits non-zero; the other four pass.
for i in "${!suites[@]}"; do
    suite="${suites[$i]}"
    marker_path="$marker_dir2/${suite%.sh}.marker"
    if [[ $i -eq 0 ]]; then
        suite_exit=1
    else
        suite_exit=0
    fi
    cat > "$mock_tests_dir/$suite" <<EOF
#!/bin/bash
: > "$marker_path"
exit $suite_exit
EOF
    chmod +x "$mock_tests_dir/$suite"
done

harness_out2="$TMP_ROOT/harness2.out"
set +e
bash "$mock_tests_dir/run-deployment-tests.sh" --verbose >"$harness_out2" 2>&1
harness_rc=$?
set -e

if [[ $harness_rc -eq 0 ]]; then
    cat "$harness_out2"
    fail "Orchestrator exit code should be non-zero when a suite fails (got 0)."
fi
pass "Dynamic-fail: orchestrator exited non-zero with a failing suite"

missing2=()
for suite in "${suites[@]}"; do
    if [[ ! -f "$marker_dir2/${suite%.sh}.marker" ]]; then
        missing2+=("$suite")
    fi
done

if (( ${#missing2[@]} > 0 )); then
    echo "Missing suite markers (mixed pass/fail): ${missing2[*]}"
    echo "Full harness output:"
    cat "$harness_out2"
    fail "Orchestrator aborted after failing suite; run_test_suite fail-path still trips set -e."
fi
pass "Dynamic-fail: all ${#suites[@]} suites invoked despite first suite failing"

plain_out2=$(sed -r $'s/\x1b\\[[0-9;]*[A-Za-z]//g' "$harness_out2")

if ! echo "$plain_out2" | grep -q "SOME TESTS FAILED"; then
    echo "$plain_out2"
    fail "Failure banner missing when a suite failed."
fi
pass "Dynamic-fail: failure banner emitted"

if ! echo "$plain_out2" | grep -Eq "Failed:[[:space:]]+1( |$)"; then
    echo "$plain_out2"
    fail "Summary did not report Failed: 1 with a single failing suite."
fi
pass "Dynamic-fail: summary reports 1 failed"

if ! echo "$plain_out2" | grep -Eq "Passed:[[:space:]]+4( |$)"; then
    echo "$plain_out2"
    fail "Summary did not report Passed: 4 with four passing suites."
fi
pass "Dynamic-fail: summary reports 4 passed"

# ----------------------------------------------------------------------------
# Static regression: test-compose-generator.sh must guard `wait $pid` in
# test_concurrent_generation_safety. Under `set -euo pipefail` a naked
# `wait $pid` propagates the child's exit code and, when two concurrent
# compose-generator processes race on the same output dir, aborts the whole
# test suite silently (no [FAIL] emitted) — the exact symptom of blocker
# #980's "concurrent generation safety" flake.
# ----------------------------------------------------------------------------

REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
COMPOSE_TEST="$REPO_ROOT/tests/test-compose-generator.sh"

if [[ ! -f "$COMPOSE_TEST" ]]; then
    fail "test-compose-generator.sh not found at $COMPOSE_TEST"
fi

# Locate test_concurrent_generation_safety start (line number) and its next
# top-level function boundary, then check every `wait $pidN` inside is guarded.
concurrent_start=$(grep -n '^test_concurrent_generation_safety[[:space:]]*()' "$COMPOSE_TEST" | head -1 | cut -d: -f1 || true)
if [[ -z "$concurrent_start" ]]; then
    fail "Missing test_concurrent_generation_safety in $COMPOSE_TEST"
fi
concurrent_end=$(awk -v start="$concurrent_start" 'NR>start && /^[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(\)/ {print NR; exit}' "$COMPOSE_TEST")
if [[ -z "$concurrent_end" ]]; then
    concurrent_end=$(wc -l < "$COMPOSE_TEST")
fi
naked_wait=$(sed -n "${concurrent_start},${concurrent_end}p" "$COMPOSE_TEST" \
    | grep -nE 'wait[[:space:]]+\$pid[0-9]+' \
    | grep -vE '\|\|[[:space:]]*true' || true)
if [[ -n "$naked_wait" ]]; then
    echo "$naked_wait"
    fail "test_concurrent_generation_safety has unguarded 'wait \$pidN' — must add '|| true' so set -e does not abort silently on legitimate concurrent-race failures."
fi
pass "Static: test_concurrent_generation_safety guards wait \$pidN with || true"

# ----------------------------------------------------------------------------
# Static regression: test-integration.sh run_all_tests must reference only
# functions that are defined in the same file. A stale reference (like the
# removed test_host_network_deployment_pipeline from commit a2aca38ff) causes
# `command not found` under set -e, silently aborting the suite mid-run.
# ----------------------------------------------------------------------------

INTEG_TEST="$REPO_ROOT/tests/test-integration.sh"
if [[ ! -f "$INTEG_TEST" ]]; then
    fail "test-integration.sh not found at $INTEG_TEST"
fi

run_all_start=$(grep -n '^run_all_tests[[:space:]]*()' "$INTEG_TEST" | head -1 | cut -d: -f1 || true)
if [[ -z "$run_all_start" ]]; then
    fail "Missing run_all_tests in $INTEG_TEST"
fi
run_all_end=$(awk -v start="$run_all_start" 'NR>start && /^[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(\)/ {print NR; exit}' "$INTEG_TEST")
if [[ -z "$run_all_end" ]]; then
    run_all_end=$(wc -l < "$INTEG_TEST")
fi
missing_fns=()
while IFS= read -r fn; do
    [[ -z "$fn" ]] && continue
    if ! grep -qE "^${fn}[[:space:]]*\(\)" "$INTEG_TEST"; then
        missing_fns+=("$fn")
    fi
done < <(sed -n "${run_all_start},${run_all_end}p" "$INTEG_TEST" \
    | grep -oE '^[[:space:]]+test_[A-Za-z0-9_]+' \
    | awk '{print $1}' \
    | sort -u)

if (( ${#missing_fns[@]} > 0 )); then
    echo "run_all_tests references undefined functions: ${missing_fns[*]}"
    fail "test-integration.sh run_all_tests calls undefined test function(s); this triggers 'command not found' and aborts the suite under set -e."
fi
pass "Static: test-integration.sh run_all_tests only references defined test functions"

# ----------------------------------------------------------------------------
# Static regression: deploy-docker.sh must derive CONNECTION_STRING and
# HTTP_PORT / HTTPS_PORT / API_PORT inside the NON_INTERACTIVE short-circuit
# paths of configure_database / configure_networking. Without this, a stale
# .deploy-config that omits these values leaves them unbound; save_deployment_config
# then references them in a heredoc opened via `cat >`, `set -u` fires, and
# .deploy-config is truncated to 0 bytes — silently dropping INCLUDE_MONITORING
# and every setting written after the first heredoc (blockers #2 / #3 / #4).
# ----------------------------------------------------------------------------

DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"
if [[ ! -f "$DEPLOY_SCRIPT" ]]; then
    fail "deploy-docker.sh not found at $DEPLOY_SCRIPT"
fi

# Helper: locate a function body's line range.
function_range() {
    local fn="$1" file="$2"
    local start end
    start=$(grep -n "^${fn}[[:space:]]*()" "$file" | head -1 | cut -d: -f1 || true)
    if [[ -z "$start" ]]; then
        echo "0 0"
        return
    fi
    end=$(awk -v s="$start" 'NR>s && /^[A-Za-z_][A-Za-z0-9_]*[[:space:]]*\(\)/ {print NR; exit}' "$file")
    if [[ -z "$end" ]]; then
        end=$(wc -l < "$file")
    fi
    echo "$start $end"
}

read -r cd_start cd_end <<<"$(function_range configure_database "$DEPLOY_SCRIPT")"
if [[ "$cd_start" == "0" ]]; then
    fail "configure_database not found in $DEPLOY_SCRIPT"
fi
cd_body=$(sed -n "${cd_start},${cd_end}p" "$DEPLOY_SCRIPT")
if ! echo "$cd_body" | grep -qE 'NON_INTERACTIVE.*=.*"true".*DB_PROVIDER'; then
    fail "configure_database missing NON_INTERACTIVE + DB_PROVIDER short-circuit guard."
fi
# Extract just the short-circuit block (up to its `return 0`).
cd_shortcircuit=$(echo "$cd_body" | awk '
    /NON_INTERACTIVE.*=.*"true".*DB_PROVIDER/ { in_block=1 }
    in_block { print }
    in_block && /return 0/ { exit }
')
if ! echo "$cd_shortcircuit" | grep -qE 'CONNECTION_STRING='; then
    echo "$cd_shortcircuit"
    fail "configure_database short-circuit does not derive CONNECTION_STRING; save_deployment_config heredoc will trip set -u."
fi
pass "Static: configure_database short-circuit derives CONNECTION_STRING"

read -r cn_start cn_end <<<"$(function_range configure_networking "$DEPLOY_SCRIPT")"
if [[ "$cn_start" == "0" ]]; then
    fail "configure_networking not found in $DEPLOY_SCRIPT"
fi
cn_body=$(sed -n "${cn_start},${cn_end}p" "$DEPLOY_SCRIPT")
if ! echo "$cn_body" | grep -qE 'NON_INTERACTIVE.*=.*"true".*NETWORK_MODE'; then
    fail "configure_networking missing NON_INTERACTIVE + NETWORK_MODE short-circuit guard."
fi
cn_shortcircuit=$(echo "$cn_body" | awk '
    /NON_INTERACTIVE.*=.*"true".*NETWORK_MODE/ { in_block=1 }
    in_block { print }
    in_block && /return 0/ { exit }
')
for port_var in HTTP_PORT HTTPS_PORT API_PORT; do
    if ! echo "$cn_shortcircuit" | grep -qE "${port_var}="; then
        echo "$cn_shortcircuit"
        fail "configure_networking short-circuit does not derive ${port_var}; save_deployment_config heredoc will trip set -u."
    fi
done
pass "Static: configure_networking short-circuit derives HTTP_PORT, HTTPS_PORT, API_PORT"

# ----------------------------------------------------------------------------
# Static regression: configure_additional must respect a pre-loaded
# ENABLE_SPOOLMAN in NON_INTERACTIVE mode. Without this guard, the interactive
# "Choose an option [1/2/3]:" prompt returns the "3" (skip) default and
# overwrites SPOOLMAN_BASE_URL with "" — dropping PFARM__Spoolman__BaseUrl
# from the generated .env even when it was set in .deploy-config.
# ----------------------------------------------------------------------------

read -r ca_start ca_end <<<"$(function_range configure_additional "$DEPLOY_SCRIPT")"
if [[ "$ca_start" == "0" ]]; then
    fail "configure_additional not found in $DEPLOY_SCRIPT"
fi
ca_body=$(sed -n "${ca_start},${ca_end}p" "$DEPLOY_SCRIPT")
if ! echo "$ca_body" | grep -qE 'NON_INTERACTIVE.*=.*"true".*ENABLE_SPOOLMAN'; then
    fail "configure_additional missing NON_INTERACTIVE + ENABLE_SPOOLMAN short-circuit guard."
fi
pass "Static: configure_additional preserves pre-loaded ENABLE_SPOOLMAN in NON_INTERACTIVE mode"

# ----------------------------------------------------------------------------
# Static regression: generate_slicer_worker_api_keys must reuse
# WORKER_SHARED_API_KEY as the primary worker's key so slicer-host ↔ worker
# auth (WORKER_SHARED_API_KEY) matches the SlicerRegistry__ApiKey the primary
# worker registers with. Otherwise two different values are written into .env
# under WORKER_SHARED_API_KEY / SlicerRegistry__ApiKey and worker job-claim
# auth fails after registration.
# ----------------------------------------------------------------------------

read -r ws_start ws_end <<<"$(function_range generate_slicer_worker_api_keys "$DEPLOY_SCRIPT")"
if [[ "$ws_start" == "0" ]]; then
    fail "generate_slicer_worker_api_keys not found in $DEPLOY_SCRIPT"
fi
ws_body=$(sed -n "${ws_start},${ws_end}p" "$DEPLOY_SCRIPT")
if ! echo "$ws_body" | grep -qE 'WORKER_SHARED_API_KEY'; then
    fail "generate_slicer_worker_api_keys does not reference WORKER_SHARED_API_KEY; primary worker key will diverge from the shared job-claim key."
fi
pass "Static: generate_slicer_worker_api_keys reuses WORKER_SHARED_API_KEY for the primary worker"

echo ""
printf '%sAll run-deployment-tests harness regressions passed%s\n' "$GREEN" "$NC"
