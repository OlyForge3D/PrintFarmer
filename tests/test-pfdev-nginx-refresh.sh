#!/bin/bash

# Regression tests for issue #3316: `pfdev deploy|redeploy <upstream>` must
# validate and reload a running nginx-proxy so it re-resolves upstream
# container IPs, and must fail loudly when validation or reload fails.
#
# The real scripts/pfdev is copied into an isolated fixture repository and run
# against a fake `docker` on PATH. No real containers are touched.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

source "$SCRIPT_DIR/test-framework.sh"

TEST_TEMP_DIR=""
FIXTURE_ROOT=""
MOCK_BIN=""
DOCKER_LOG=""
PFDEV_OUTPUT=""
PFDEV_RC=0

setup() {
    TEST_TEMP_DIR=$(create_test_temp_dir)
    FIXTURE_ROOT="$TEST_TEMP_DIR/repo"
    MOCK_BIN="$TEST_TEMP_DIR/bin"
    DOCKER_LOG="$TEST_TEMP_DIR/docker.log"

    mkdir -p "$FIXTURE_ROOT/scripts" "$MOCK_BIN"
    cp "$REPO_ROOT/scripts/pfdev" "$REPO_ROOT/scripts/build-metadata.sh" \
        "$REPO_ROOT/scripts/common-utils.sh" "$FIXTURE_ROOT/scripts/"
    chmod +x "$FIXTURE_ROOT/scripts/pfdev"

    # Certificate generator stub: frontend/nginx deploys call it when the
    # fixture has no certificates; it must never touch real certificates.
    printf '#!/bin/bash\nexit 0\n' > "$FIXTURE_ROOT/scripts/generate-certs.sh"
    chmod +x "$FIXTURE_ROOT/scripts/generate-certs.sh"

    : > "$FIXTURE_ROOT/Dockerfile.multistage"
    : > "$FIXTURE_ROOT/docker-entrypoint-config.sh"
    cat > "$FIXTURE_ROOT/docker-compose.yml" <<'EOF'
services:
  api:
    build:
      context: .
  frontend:
    build:
      context: .
  nginx-proxy:
    build:
      context: .
  database:
    image: postgres:16
EOF

    git -C "$FIXTURE_ROOT" init -q
    git -C "$FIXTURE_ROOT" -c user.name=test -c user.email=test@example.invalid \
        commit -q --allow-empty -m fixture

    cat > "$MOCK_BIN/docker" <<'EOF'
#!/bin/bash
set -euo pipefail

printf '%s\n' "$*" >> "$MOCK_DOCKER_LOG"

if [[ "${1:-}" != "compose" ]]; then
    exit 0
fi
shift

case "$*" in
    "config --services")
        printf '%s\n' api frontend database
        if [[ "${MOCK_NO_PROXY_SERVICE:-false}" != "true" ]]; then
            echo nginx-proxy
        fi
        ;;
    up\ *)
        exit "${MOCK_UP_RC:-0}"
        ;;
    "ps --status running --quiet nginx-proxy")
        if [[ "${MOCK_PS_RC:-0}" != "0" ]]; then
            echo "Cannot connect to the Docker daemon" >&2
            exit "$MOCK_PS_RC"
        fi
        if [[ "${MOCK_PROXY_RUNNING:-true}" == "true" ]]; then
            echo "0123456789ab"
        fi
        ;;
    "exec -T nginx-proxy nginx -t")
        if [[ "${MOCK_NGINX_TEST_RC:-0}" != "0" ]]; then
            echo 'nginx: [emerg] host not found in upstream "api:5245"' >&2
            exit "$MOCK_NGINX_TEST_RC"
        fi
        echo "nginx: configuration file /etc/nginx/nginx.conf test is successful" >&2
        ;;
    "exec -T nginx-proxy nginx -s reload")
        if [[ "${MOCK_RELOAD_RC:-0}" != "0" ]]; then
            echo 'nginx: [error] invalid PID number "" in "/run/nginx.pid"' >&2
            exit "$MOCK_RELOAD_RC"
        fi
        ;;
esac
exit 0
EOF
    chmod +x "$MOCK_BIN/docker"
}

teardown() {
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
}

# Run the fixture pfdev with the fake docker. Extra env assignments are passed
# as leading arguments (NAME=value ...) before the pfdev arguments.
run_pfdev() {
    local env_args=()
    while [[ $# -gt 0 && "$1" == *=* ]]; do
        env_args+=("$1")
        shift
    done

    : > "$DOCKER_LOG"
    set +e
    PFDEV_OUTPUT=$(env PATH="$MOCK_BIN:$PATH" MOCK_DOCKER_LOG="$DOCKER_LOG" \
        "${env_args[@]}" bash "$FIXTURE_ROOT/scripts/pfdev" "$@" 2>&1)
    PFDEV_RC=$?
    set -e
}

docker_calls() {
    cat "$DOCKER_LOG"
}

# Line numbers of the first log entry matching a fixed string (0 if absent).
first_call_line() {
    grep -nF -- "$1" "$DOCKER_LOG" | head -n 1 | cut -d: -f1 || true
}

test_redeploy_frontend_validates_then_reloads_nginx() {
    start_test "redeploy frontend validates and reloads running nginx-proxy after up"

    run_pfdev redeploy frontend
    local calls up_line test_line reload_line
    calls=$(docker_calls)
    up_line=$(first_call_line "compose up -d --remove-orphans frontend")
    test_line=$(first_call_line "compose exec -T nginx-proxy nginx -t")
    reload_line=$(first_call_line "compose exec -T nginx-proxy nginx -s reload")

    assert_equals "0" "$PFDEV_RC" "redeploy should succeed: $PFDEV_OUTPUT" || return 0
    assert_contains "$calls" "compose build --no-cache frontend" || return 0
    assert_not_equals "" "$up_line" "frontend must be brought up" || return 0
    assert_not_equals "" "$test_line" "nginx config must be validated" || return 0
    assert_not_equals "" "$reload_line" "nginx must be reloaded" || return 0
    if (( up_line >= test_line || test_line >= reload_line )); then
        fail_test "expected order up < nginx -t < reload, got $up_line/$test_line/$reload_line"
        return 0
    fi
    assert_contains "$PFDEV_OUTPUT" "nginx reloaded" || return 0
    pass_test
}

test_deploy_api_reloads_nginx() {
    start_test "deploy api reloads running nginx-proxy"

    run_pfdev deploy api
    assert_equals "0" "$PFDEV_RC" "deploy should succeed: $PFDEV_OUTPUT" || return 0
    assert_contains "$(docker_calls)" "compose exec -T nginx-proxy nginx -s reload" || return 0
    pass_test
}

test_deploy_nginx_alias_still_reloads() {
    start_test "deploy nginx alias force-recreates and reloads via compose"

    run_pfdev deploy nginx
    local calls
    calls=$(docker_calls)
    assert_equals "0" "$PFDEV_RC" "deploy should succeed: $PFDEV_OUTPUT" || return 0
    assert_contains "$calls" "compose up -d --remove-orphans --force-recreate nginx-proxy" || return 0
    assert_contains "$calls" "compose exec -T nginx-proxy nginx -s reload" || return 0
    pass_test
}

test_invalid_config_fails_without_reload() {
    start_test "failed nginx -t fails deploy, prints the error, and skips reload"

    run_pfdev MOCK_NGINX_TEST_RC=1 deploy frontend
    assert_not_equals "0" "$PFDEV_RC" "deploy must fail when nginx -t fails" || return 0
    assert_contains "$PFDEV_OUTPUT" "nginx configuration test failed" || return 0
    assert_contains "$PFDEV_OUTPUT" 'host not found in upstream "api:5245"' || return 0
    assert_not_contains "$(docker_calls)" "nginx -s reload" "reload must not run after failed validation" || return 0
    assert_not_contains "$PFDEV_OUTPUT" "Successfully deployed" || return 0
    pass_test
}

test_reload_failure_is_surfaced() {
    start_test "failed nginx reload fails deploy and prints the error"

    run_pfdev MOCK_RELOAD_RC=1 redeploy frontend
    assert_not_equals "0" "$PFDEV_RC" "redeploy must fail when reload fails" || return 0
    assert_contains "$PFDEV_OUTPUT" "nginx reload failed" || return 0
    assert_contains "$PFDEV_OUTPUT" "invalid PID number" || return 0
    assert_not_contains "$PFDEV_OUTPUT" "Successfully redeployed" || return 0
    pass_test
}

test_proxy_status_query_failure_is_surfaced() {
    start_test "failure to query nginx-proxy status fails deploy"

    run_pfdev MOCK_PS_RC=1 deploy api
    assert_not_equals "0" "$PFDEV_RC" "deploy must fail when proxy status is unknown" || return 0
    assert_contains "$PFDEV_OUTPUT" "Could not query nginx-proxy status" || return 0
    pass_test
}

test_stopped_proxy_is_skipped_for_upstream() {
    start_test "stopped nginx-proxy is skipped (not an error) for upstream deploys"

    run_pfdev MOCK_PROXY_RUNNING=false deploy api
    assert_equals "0" "$PFDEV_RC" "deploy should succeed: $PFDEV_OUTPUT" || return 0
    assert_contains "$PFDEV_OUTPUT" "nginx-proxy is not running; skipping nginx refresh" || return 0
    assert_not_contains "$(docker_calls)" "compose exec" || return 0
    pass_test
}

test_stopped_proxy_after_nginx_deploy_fails() {
    start_test "nginx deploy fails when nginx-proxy is not running afterwards"

    run_pfdev MOCK_PROXY_RUNNING=false deploy nginx
    assert_not_equals "0" "$PFDEV_RC" "nginx deploy must fail if the proxy is not running" || return 0
    assert_contains "$PFDEV_OUTPUT" "nginx-proxy is not running after deploy" || return 0
    pass_test
}

test_stack_without_proxy_skips_refresh() {
    start_test "stacks without an nginx-proxy service skip refresh"

    run_pfdev MOCK_NO_PROXY_SERVICE=true deploy api
    assert_equals "0" "$PFDEV_RC" "deploy should succeed: $PFDEV_OUTPUT" || return 0
    assert_not_contains "$(docker_calls)" "nginx-proxy" || return 0
    pass_test
}

test_compose_up_failure_is_surfaced() {
    start_test "docker compose up failure fails deploy without refreshing nginx"

    run_pfdev MOCK_UP_RC=1 deploy api
    assert_not_equals "0" "$PFDEV_RC" "deploy must fail when compose up fails" || return 0
    assert_contains "$PFDEV_OUTPUT" "docker compose up failed for api" || return 0
    assert_not_contains "$(docker_calls)" "compose exec" || return 0
    pass_test
}

run_tests() {
    test_redeploy_frontend_validates_then_reloads_nginx
    test_deploy_api_reloads_nginx
    test_deploy_nginx_alias_still_reloads
    test_invalid_config_fails_without_reload
    test_reload_failure_is_surfaced
    test_proxy_status_query_failure_is_surfaced
    test_stopped_proxy_is_skipped_for_upstream
    test_stopped_proxy_after_nginx_deploy_fails
    test_stack_without_proxy_skips_refresh
    test_compose_up_failure_is_surfaced
}

setup
trap teardown EXIT
run_test_suite run_tests "pfdev nginx upstream refresh (issue #3316)"
