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

echo ""
printf '%sAll run-deployment-tests harness regressions passed%s\n' "$GREEN" "$NC"
