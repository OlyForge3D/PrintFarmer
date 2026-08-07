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

# The sub-suite scripts referenced by run_full_tests. Order matters:
# the historical bug aborted after the first one succeeded, so seeing every
# marker present proves the orchestrator kept going.
suites=(
    test-compose-generator.sh
    test-deploy-docker.sh
    test-validate-deployment-scripts.sh
    test-orcaslicer-binary-metadata.sh
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

# The static layer must reject the historical root-relative access pattern,
# including a transient create/delete that would leave no final-state delta.
root_relative_test="$mock_tests_dir/test-root-relative-access.sh"
cat > "$root_relative_test" <<'EOF'
#!/bin/bash
cd "$REPO_ROOT"
source .deploy-config
: > .env
rm -f .env
EOF
chmod +x "$root_relative_test"

root_relative_out="$TMP_ROOT/root-relative.out"
set +e
bash "$mock_tests_dir/run-deployment-tests.sh" --quick >"$root_relative_out" 2>&1
root_relative_rc=$?
set -e

if [[ $root_relative_rc -eq 0 ]]; then
    cat "$root_relative_out"
    fail "Static isolation guard accepted repo-root-relative artifact access."
fi
if ! grep -q "forbidden repo-root artifact access" "$root_relative_out"; then
    cat "$root_relative_out"
    fail "Static isolation failure did not identify repo-root-relative access."
fi
rm -f "$root_relative_test"
pass "Static-isolation: repo-root-relative source and transient mutation are rejected"

# The same access embedded in a quoted command string is the historical suite
# pattern and must be rejected independently of the bare-shell form above.
quoted_root_relative_test="$mock_tests_dir/test-quoted-root-relative-access.sh"
cat > "$quoted_root_relative_test" <<'EOF'
#!/bin/bash
capture_output "cd '$REPO_ROOT' && source .deploy-config; : > .env; rm -f .env"
EOF
chmod +x "$quoted_root_relative_test"

quoted_root_relative_out="$TMP_ROOT/quoted-root-relative.out"
set +e
bash "$mock_tests_dir/run-deployment-tests.sh" --quick >"$quoted_root_relative_out" 2>&1
quoted_root_relative_rc=$?
set -e

if [[ $quoted_root_relative_rc -eq 0 ]]; then
    cat "$quoted_root_relative_out"
    fail "Static isolation guard accepted quoted repo-root-relative artifact access."
fi
if ! grep -q "forbidden repo-root artifact access" "$quoted_root_relative_out"; then
    cat "$quoted_root_relative_out"
    fail "Static isolation failure did not identify quoted repo-root-relative access."
fi
rm -f "$quoted_root_relative_test"
pass "Static-isolation: quoted repo-root-relative command strings are rejected"

harness_out="$TMP_ROOT/harness.out"
sentinel_config="$TMP_ROOT/.deploy-config"
printf '%s\n' \
    "ARCHITECTURE=microservices" \
    "ENABLE_ORCA_WORKER=yes" \
    "ORCA_WORKER_COUNT=1" > "$sentinel_config"
sentinel_hash_before=$(sha256sum "$sentinel_config" | awk '{print $1}')
sentinel_size_before=$(wc -c < "$sentinel_config" | tr -d ' ')
sentinel_mtime_before=$(stat -c '%y' "$sentinel_config")

if ! bash "$mock_tests_dir/run-deployment-tests.sh" --verbose >"$harness_out" 2>&1; then
    echo "Harness exit non-zero with mocked (always-passing) sub-suites. Output:"
    cat "$harness_out"
    fail "Orchestrator aborted with mocked passing sub-suites; likely a set -e counter regression."
fi
pass "Dynamic: orchestrator exited 0 with mocked passing sub-suites"

sentinel_hash_after=$(sha256sum "$sentinel_config" | awk '{print $1}')
sentinel_size_after=$(wc -c < "$sentinel_config" | tr -d ' ')
sentinel_mtime_after=$(stat -c '%y' "$sentinel_config")
if [[ "$sentinel_hash_after" != "$sentinel_hash_before" \
    || "$sentinel_size_after" != "$sentinel_size_before" \
    || "$sentinel_mtime_after" != "$sentinel_mtime_before" ]]; then
    fail "Passing deployment suites changed the pre-existing repo-root .deploy-config sentinel."
fi
pass "Dynamic: pre-existing repo-root .deploy-config remains byte- and mtime-identical"

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

# Sanity: the summary must report every mocked suite passed with none failed.
suite_count=${#suites[@]}
if ! echo "$plain_out" | grep -Eq "Total Tests Run:[[:space:]]+${suite_count}( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Total Tests Run: ${suite_count}."
fi
pass "Dynamic: summary reports ${suite_count} total tests run"

if ! echo "$plain_out" | grep -Eq "Passed:[[:space:]]+${suite_count}( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Passed: ${suite_count}."
fi
pass "Dynamic: summary reports ${suite_count} passed"

if ! echo "$plain_out" | grep -Eq "Failed:[[:space:]]+0( |$)"; then
    echo "$plain_out"
    fail "Summary did not report Failed: 0."
fi
pass "Dynamic: summary reports 0 failed"

# ----------------------------------------------------------------------------
# Dynamic regression #2: mixed pass/fail. The orchestrator must run all
# suites even when the first one fails. Historical variant of the same
# set-e harness defect: run_test_suite returning 1 tripped errexit in
# run_full_tests and skipped the remaining suites. FAILED_TESTS and
# print_summary already carry the pass/fail signal, so suppressing the
# caller-side abort is safe.
# ----------------------------------------------------------------------------

marker_dir2="$TMP_ROOT/markers2"
mkdir -p "$marker_dir2"

# Suite #1 drops its marker then exits non-zero; every other suite passes.
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

expected_passes=$((suite_count - 1))
if ! echo "$plain_out2" | grep -Eq "Passed:[[:space:]]+${expected_passes}( |$)"; then
    echo "$plain_out2"
    fail "Summary did not report Passed: ${expected_passes}."
fi
pass "Dynamic-fail: summary reports ${expected_passes} passed"

# ----------------------------------------------------------------------------
# Dynamic regression #3: a downstream suite that mutates a protected
# repo-root deployment artifact must make the orchestrator fail, even if the
# suite itself exits successfully.
# ----------------------------------------------------------------------------

for suite in "${suites[@]}"; do
    cat > "$mock_tests_dir/$suite" <<'EOF'
#!/bin/bash
exit 0
EOF
    chmod +x "$mock_tests_dir/$suite"
done

cat > "$mock_tests_dir/test-deploy-docker.sh" <<EOF
#!/bin/bash
printf '%s\n' 'ORCA_WORKER_COUNT=99' > "$sentinel_config"
exit 0
EOF
chmod +x "$mock_tests_dir/test-deploy-docker.sh"

harness_out3="$TMP_ROOT/harness3.out"
set +e
bash "$mock_tests_dir/run-deployment-tests.sh" --verbose >"$harness_out3" 2>&1
harness_rc3=$?
set -e

if [[ $harness_rc3 -eq 0 ]]; then
    cat "$harness_out3"
    fail "Repository artifact isolation guard accepted a mutating suite."
fi
if ! grep -q "mutated repo-root deployment artifacts" "$harness_out3"; then
    cat "$harness_out3"
    fail "Repository artifact isolation failure did not identify the mutation."
fi
pass "Dynamic-isolation: repo-root deployment artifact mutation fails the orchestrator"

# ----------------------------------------------------------------------------
# Dynamic regression #4: protected generated directories must be recursively
# fingerprinted so in-place descendant edits cannot hide behind directory stat.
# ----------------------------------------------------------------------------

for suite in "${suites[@]}"; do
    cat > "$mock_tests_dir/$suite" <<'EOF'
#!/bin/bash
exit 0
EOF
    chmod +x "$mock_tests_dir/$suite"
done

nested_artifact="$TMP_ROOT/monitoring/prometheus/prometheus.yml"
mkdir -p "$(dirname "$nested_artifact")"
printf '%s\n' 'global: baseline' > "$nested_artifact"

cat > "$mock_tests_dir/test-deploy-docker.sh" <<EOF
#!/bin/bash
printf '%s\n' 'global: mutated' > "$nested_artifact"
exit 0
EOF
chmod +x "$mock_tests_dir/test-deploy-docker.sh"

harness_out4="$TMP_ROOT/harness4.out"
set +e
bash "$mock_tests_dir/run-deployment-tests.sh" --verbose >"$harness_out4" 2>&1
harness_rc4=$?
set -e

if [[ $harness_rc4 -eq 0 ]]; then
    cat "$harness_out4"
    fail "Repository artifact isolation guard accepted a nested artifact mutation."
fi
if ! grep -q "monitoring/prometheus/prometheus.yml" "$harness_out4"; then
    cat "$harness_out4"
    fail "Repository artifact isolation failure did not identify the nested mutation."
fi
rm -rf "$TMP_ROOT/monitoring"
pass "Dynamic-isolation: nested protected artifact mutation fails the orchestrator"

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
# Static regression: deployment config supplies only the bootstrap key, while
# every worker process derives a fresh identity at runtime.
# ----------------------------------------------------------------------------

for compose_file in \
    "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml" \
    "$REPO_ROOT/scripts/docker/compose-templates/docker-compose.orcaslicer-worker-previous.yml"; do
    if ! grep -q 'WorkerAuth__SharedKey=${WORKER_SHARED_API_KEY:-}' "$compose_file"; then
        fail "$(basename "$compose_file") does not map the canonical WorkerAuth__SharedKey."
    fi
    if grep -qE 'WorkerAuth__SharedApiKey|Worker__SharedKey' "$compose_file"; then
        fail "$(basename "$compose_file") still contains a deprecated worker-auth alias."
    fi
    if grep -q 'Worker__InstanceId' "$compose_file"; then
        fail "$(basename "$compose_file") pins a reusable worker process identity."
    fi
done
pass "Static: worker compose templates use only WorkerAuth__SharedKey and runtime identities"

# The bootstrap key must be resolved before save_deployment_config persists the
# config and before generate_env_file truncates .env.
main_start=$(grep -n '^main[[:space:]]*()' "$DEPLOY_SCRIPT" | head -1 | cut -d: -f1 || true)
resolve_line=$(awk -v start="$main_start" 'NR > start && /resolve_worker_shared_api_key/ { print NR; exit }' "$DEPLOY_SCRIPT")
save_line=$(awk -v start="$main_start" 'NR > start && /save_deployment_config/ { print NR; exit }' "$DEPLOY_SCRIPT")
if [[ -z "$resolve_line" || -z "$save_line" || "$resolve_line" -ge "$save_line" ]]; then
    fail "main must resolve WORKER_SHARED_API_KEY before save_deployment_config."
fi

read -r env_start env_end <<<"$(function_range generate_env_file "$DEPLOY_SCRIPT")"
if [[ "$env_start" == "0" ]]; then
    fail "generate_env_file not found in $DEPLOY_SCRIPT"
fi
env_body=$(sed -n "${env_start},${env_end}p" "$DEPLOY_SCRIPT")
env_resolve_line=$(echo "$env_body" | grep -n 'resolve_worker_shared_api_key' | head -1 | cut -d: -f1 || true)
env_truncate_line=$(echo "$env_body" | grep -n 'cat > "\$ENV_FILE"' | head -1 | cut -d: -f1 || true)
if [[ -z "$env_resolve_line" || -z "$env_truncate_line" || "$env_resolve_line" -ge "$env_truncate_line" ]]; then
    fail "generate_env_file must recover WORKER_SHARED_API_KEY before truncating .env."
fi
pass "Static: worker bootstrap key is resolved before config and environment rewrites"

# Exercise the resolver twice without Docker: the first pass generates and
# persists a key, and the second process-style pass must recover the same value
# before the environment file is truncated.
key_test_script="$TMP_ROOT/test-worker-key-resolution.sh"
{
    echo '#!/bin/bash'
    echo 'set -euo pipefail'
    echo 'print_info() { printf "%s\n" "$*"; }'
    for function_name in get_kv_from_file generate_slicer_api_key resolve_worker_shared_api_key; do
        read -r function_start function_end <<<"$(function_range "$function_name" "$DEPLOY_SCRIPT")"
        if [[ "$function_start" == "0" ]]; then
            fail "$function_name not found in $DEPLOY_SCRIPT"
        fi
        function_end=$((function_end - 1))
        sed -n "${function_start},${function_end}p" "$DEPLOY_SCRIPT"
    done
    cat <<'EOF'
CONFIG_FILE="$1/.deploy-config"
ENV_FILE="$1/.env"
resolve_worker_shared_api_key
first_key="$WORKER_SHARED_API_KEY"
printf 'WORKER_SHARED_API_KEY=%s\n' "$first_key" > "$CONFIG_FILE"
printf 'WORKER_SHARED_API_KEY=%s\n' "$first_key" > "$ENV_FILE"
unset WORKER_SHARED_API_KEY
resolve_worker_shared_api_key
second_key="$WORKER_SHARED_API_KEY"
[[ "$second_key" == "$first_key" ]]
: > "$ENV_FILE"
printf 'WORKER_SHARED_API_KEY=%s\n' "$second_key" > "$ENV_FILE"
[[ "$(get_kv_from_file "$ENV_FILE" WORKER_SHARED_API_KEY)" == "$first_key" ]]
EOF
} > "$key_test_script"
chmod +x "$key_test_script"

key_test_output="$TMP_ROOT/worker-key-resolution.out"
if ! bash "$key_test_script" "$TMP_ROOT" > "$key_test_output" 2>&1; then
    cat "$key_test_output"
    fail "Worker bootstrap key changed across two resolution passes."
fi
resolved_key=$(grep '^WORKER_SHARED_API_KEY=' "$TMP_ROOT/.env" | tail -1 | cut -d= -f2-)
if grep -Fq "$resolved_key" "$key_test_output"; then
    fail "Worker bootstrap key resolver emitted secret material."
fi
pass "Dynamic: worker bootstrap key survives two resolution passes without log disclosure"

echo ""
printf '%sAll run-deployment-tests harness regressions passed%s\n' "$GREEN" "$NC"
