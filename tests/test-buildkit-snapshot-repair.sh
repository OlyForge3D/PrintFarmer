#!/bin/bash

# Regression tests for the BuildKit/containerd snapshot corruption auto-repair
# added for issue #1527 (is_buildkit_snapshot_corruption /
# run_compose_build_with_snapshot_repair in scripts/deploy-docker.sh).
#
# These tests exist specifically to catch the argv-ordering class of bug found
# during review: a naive implementation that appends `--no-cache` to the end
# of the retry command breaks call sites (like the OrcaSlicer `docker build`
# invocation) that already end with a positional build-context path (`.`).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

source "$SCRIPT_DIR/test-framework.sh"

TEST_TEMP_DIR=""
MOCK_BIN=""

setup() {
    TEST_TEMP_DIR=$(create_test_temp_dir)
    MOCK_BIN="$TEST_TEMP_DIR/bin"
    mkdir -p "$MOCK_BIN"
}

teardown() {
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
}

# Install a mock `docker` on PATH that:
#   - Logs its full argv (one invocation per line) to $MOCK_DOCKER_ARGV_LOG.
#   - Fails on its first invocation, emitting the exact three-line BuildKit
#     snapshot-corruption signature from issue #1527 (unless
#     MOCK_ALWAYS_FAIL=true, in which case every invocation fails the same
#     way, simulating an unrelated/deeper failure that the retry cannot fix).
#   - Succeeds on every subsequent invocation.
install_snapshot_corruption_mock_docker() {
    cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
set -euo pipefail

if [[ -n "${MOCK_DOCKER_ARGV_LOG:-}" ]]; then
    printf '%s\n' "$*" >> "$MOCK_DOCKER_ARGV_LOG"
fi

call_count_file="${MOCK_DOCKER_CALL_COUNT_FILE:?MOCK_DOCKER_CALL_COUNT_FILE must be set}"
count=0
if [[ -f "$call_count_file" ]]; then
    count=$(<"$call_count_file")
fi
count=$((count + 1))
printf '%s' "$count" > "$call_count_file"

if [[ "${MOCK_ALWAYS_FAIL:-false}" == "true" || "$count" -eq 1 ]]; then
    echo "failed to commit j9iyc3dyh2304nlj1lsnherqr to n7d4mud8wdonask9boa3gdn4q during finalize:" >&2
    echo "failed to stat active key during commit:" >&2
    echo "snapshot j9iyc3dyh2304nlj1lsnherqr does not exist: not found" >&2
    exit 1
fi

exit 0
EOF
    chmod +x "$MOCK_BIN/docker"
}

# ---------------------------------------------------------------------------
# is_buildkit_snapshot_corruption
# ---------------------------------------------------------------------------

test_signature_matches_known_error() {
    start_test "is_buildkit_snapshot_corruption recognizes the known BuildKit error signature"

    local rc
    set +e
    (
        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1

        log_file="$TEST_TEMP_DIR/matching.log"
        cat > "$log_file" <<'EOF'
Step 12/20 : COPY VERSION ./
failed to commit j9iyc3dyh2304nlj1lsnherqr to n7d4mud8wdonask9boa3gdn4q during finalize:
failed to stat active key during commit:
snapshot j9iyc3dyh2304nlj1lsnherqr does not exist: not found
EOF
        is_buildkit_snapshot_corruption "$log_file"
    )
    rc=$?
    set -e

    assert_equals "0" "$rc" "expected the three-line signature to be recognized"
    pass_test
}

test_unrelated_failure_does_not_match() {
    start_test "is_buildkit_snapshot_corruption ignores unrelated build failures"

    local rc
    set +e
    (
        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1

        log_file="$TEST_TEMP_DIR/unrelated.log"
        cat > "$log_file" <<'EOF'
Step 8/20 : RUN dotnet restore
error MSB4018: The "Restore" task failed unexpectedly.
EOF
        is_buildkit_snapshot_corruption "$log_file"
    )
    rc=$?
    set -e

    assert_not_equals "0" "$rc" "an unrelated build failure must not be misclassified as the BuildKit snapshot bug"
    pass_test
}

# ---------------------------------------------------------------------------
# run_compose_build_with_snapshot_repair
# ---------------------------------------------------------------------------

# Regression test for the reviewer-found argv-ordering bug: the OrcaSlicer
# `docker build` call ends with a positional build-context path (`.`).
# Blindly appending `--no-cache` at the end of the retry produces the invalid
# invocation `docker build ... . --no-cache`. The fix must insert --no-cache
# immediately after the literal `build` token instead.
test_retry_inserts_no_cache_before_trailing_build_context() {
    start_test "retry inserts --no-cache after 'build', not after a trailing build-context path"
    install_snapshot_corruption_mock_docker

    local rc
    set +e
    (
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_DOCKER_ARGV_LOG="$TEST_TEMP_DIR/argv-orca.log"
        export MOCK_DOCKER_CALL_COUNT_FILE="$TEST_TEMP_DIR/count-orca"
        : > "$MOCK_DOCKER_ARGV_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        # Sourcing deploy-docker.sh itself invokes `docker info` (environment
        # probing); reset the argv log and call counter afterward so only the
        # build/retry invocations under test are captured and the first build
        # call is still treated as "call 1" (the one that fails).
        : > "$MOCK_DOCKER_ARGV_LOG"
        rm -f "$MOCK_DOCKER_CALL_COUNT_FILE"

        # Mirrors the shape of ORCA_BUILD_CMD: ends with a positional context path.
        cmd=(docker build --platform linux/amd64 -f Dockerfile.multistage \
             --target orcaslicer-binaries -t "orcaslicer-binaries:test" .)
        run_compose_build_with_snapshot_repair "${cmd[@]}"
    )
    rc=$?
    set -e

    assert_equals "0" "$rc" "wrapper must succeed once the --no-cache retry runs against the mock"

    argv_log="$TEST_TEMP_DIR/argv-orca.log"
    assert_file_exists "$argv_log" "mock docker must have logged at least one invocation"

    first_call=$(sed -n '1p' "$argv_log")
    second_call=$(sed -n '2p' "$argv_log")

    assert_contains "$first_call" "build --platform linux/amd64" "first (failing) call is the original, unmodified command"
    assert_not_contains "$first_call" "--no-cache" "first call must not already carry --no-cache"

    # This is the key regression assertion: --no-cache must land right after
    # "build", never after the trailing "." build-context argument.
    assert_contains "$second_call" "build --no-cache --platform linux/amd64" \
        "retry must insert --no-cache immediately after 'build'"
    assert_not_contains "$second_call" ". --no-cache" \
        "retry must NOT append --no-cache after the trailing build-context path"

    pass_test
}

test_retry_inserts_no_cache_for_compose_build_shape() {
    start_test "retry inserts --no-cache after 'build' for a docker compose build shape"
    install_snapshot_corruption_mock_docker

    local rc
    set +e
    (
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_DOCKER_ARGV_LOG="$TEST_TEMP_DIR/argv-compose.log"
        export MOCK_DOCKER_CALL_COUNT_FILE="$TEST_TEMP_DIR/count-compose"
        : > "$MOCK_DOCKER_ARGV_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        : > "$MOCK_DOCKER_ARGV_LOG"
        rm -f "$MOCK_DOCKER_CALL_COUNT_FILE"

        # Mirrors a `docker compose ... build ...` invocation: "build" is
        # followed by further flags, not a trailing positional path.
        cmd=(docker compose -f docker-compose.yml --progress=plain build \
             --build-arg "BUILD_VERBOSITY=quiet" --platform linux/amd64)
        run_compose_build_with_snapshot_repair "${cmd[@]}"
    )
    rc=$?
    set -e

    assert_equals "0" "$rc" "wrapper must succeed once the --no-cache retry runs against the mock"

    argv_log="$TEST_TEMP_DIR/argv-compose.log"
    second_call=$(sed -n '2p' "$argv_log")

    assert_contains "$second_call" "build --no-cache --build-arg" \
        "retry must insert --no-cache immediately after 'build' for compose invocations too"

    pass_test
}

test_retry_failure_still_reports_error_and_propagates_rc() {
    start_test "if the --no-cache retry also fails, the wrapper reports the error and returns non-zero"
    install_snapshot_corruption_mock_docker

    local rc
    set +e
    (
        export PATH="$MOCK_BIN:$PATH"
        export MOCK_DOCKER_ARGV_LOG="$TEST_TEMP_DIR/argv-stillfail.log"
        export MOCK_DOCKER_CALL_COUNT_FILE="$TEST_TEMP_DIR/count-stillfail"
        export MOCK_ALWAYS_FAIL=true
        : > "$MOCK_DOCKER_ARGV_LOG"

        # shellcheck disable=SC1091
        source "$REPO_ROOT/scripts/deploy-docker.sh" >/dev/null 2>&1
        : > "$MOCK_DOCKER_ARGV_LOG"
        rm -f "$MOCK_DOCKER_CALL_COUNT_FILE"

        cmd=(docker build -f Dockerfile.multistage -t "orcaslicer-binaries:test" .)
        run_compose_build_with_snapshot_repair "${cmd[@]}" 2>&1
    )
    rc=$?
    set -e

    assert_not_equals "0" "$rc" "wrapper must propagate failure when the --no-cache retry does not fix the build"

    argv_log="$TEST_TEMP_DIR/argv-stillfail.log"
    call_count=$(wc -l < "$argv_log")
    assert_equals "2" "$call_count" "wrapper must attempt exactly one retry, not loop indefinitely"

    pass_test
}

run_tests() {
    test_signature_matches_known_error
    test_unrelated_failure_does_not_match
    test_retry_inserts_no_cache_before_trailing_build_context
    test_retry_inserts_no_cache_for_compose_build_shape
    test_retry_failure_still_reports_error_and_propagates_rc
}

setup
trap teardown EXIT
run_test_suite run_tests "BuildKit snapshot corruption auto-repair (issue #1527)"
