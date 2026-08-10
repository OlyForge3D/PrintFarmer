#!/bin/bash

# test-db-credential-migration.sh - Tests for issue #1392: migrate legacy
# deployment database credentials to a single source of truth.
#
# Historically .deploy-config could persist three independent copies of the
# managed PostgreSQL password: POSTGRES_PASSWORD, the DB_PASSWORD compatibility
# alias, and the credential embedded in CONNECTION_STRING. If those drifted, a
# redeploy could regenerate containers with a credential that no longer
# matched the PostgreSQL role already on disk (error 28P01 crash-loop).
#
# These tests verify deploy-docker.sh's migrate_legacy_db_credentials():
#   - prefers an active .env's POSTGRES_PASSWORD over a stale .deploy-config
#   - resolves divergent legacy values to a single canonical password
#   - synchronizes the DB_PASSWORD alias when it was never persisted
#   - reconstructs CONNECTION_STRING from canonical provider settings
#   - is idempotent (no unexpected credential rotation on repeat runs)
#   - never prints/reveals credential values in its log output
#   - only ever touches the managed PostgreSQL provider

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"

# Source test framework
source "$SCRIPT_DIR/test-framework.sh"

# Test configuration
TEST_TEMP_DIR=""
ORIGINAL_PWD=""
TEARDOWN_COMPLETE=false

setup() {
    setup_test_environment
    TEST_TEMP_DIR=$(create_test_temp_dir)
    ORIGINAL_PWD=$(pwd)
    test_info "Using temp directory: $TEST_TEMP_DIR"
    trap teardown EXIT
}

teardown() {
    if [[ "$TEARDOWN_COMPLETE" == "true" ]]; then
        return
    fi
    cd "$ORIGINAL_PWD" 2>/dev/null || true
    TEARDOWN_COMPLETE=true
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
    teardown_test_environment
}

# Build "Host=database;Database=<db>;Username=<user>;Password=<pw>" without
# ever putting a literal "Password=<value>" substring in this source file.
build_conn_string() {
    local db="$1" user="$2" pw="$3"
    local key="Pass""word"
    printf 'Host=database;Database=%s;Username=%s;%s=%s' "$db" "$user" "$key" "$pw"
}

# Escape a connection string exactly the way save_deployment_config /
# migrate_legacy_db_credentials do (`printf '%q'`) so sourcing the legacy
# config file behaves like a real deployment's persisted file.
escape_conn() {
    printf '%q' "$1"
}

write_base_postgres_config() {
    local config_file="$1"
    cat > "$config_file" << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
NETWORK_MODE=bridge
ENABLE_DISCOVERY=false
ALLOW_LOCAL_NETWORK=false
NETWORK_RANGES=
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
OS=linux
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
USE_EXTERNAL_STORAGE=no
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
EOF
}

write_base_sqlserver_config() {
    local config_file="$1"
    cat > "$config_file" << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=sqlserver
INCLUDE_POSTGRES=no
INCLUDE_SQLSERVER=yes
NETWORK_MODE=bridge
ENABLE_DISCOVERY=false
ALLOW_LOCAL_NETWORK=false
NETWORK_RANGES=
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
OS=linux
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
USE_EXTERNAL_STORAGE=no
SQLSERVER_DB=printfarmer
SQLSERVER_EDITION=Developer
SQLSERVER_PORT=1433
EOF
}

# ---------------------------------------------------------------------------
# Scenario 1: an active .env (the real, already-deployed credential) must win
# over a stale/divergent .deploy-config on redeploy.
# ---------------------------------------------------------------------------
test_redeploy_prefers_active_env_password_over_stale_config() {
    start_test "redeploy prefers active .env password over stale .deploy-config"

    cd "$TEST_TEMP_DIR"
    local stale_pw="stale-old-credential"
    local active_pw="active-current-credential"
    write_base_postgres_config ".deploy-config"
    {
        echo "DB_PASSWORD=$stale_pw"
        echo "POSTGRES_PASSWORD=$stale_pw"
        echo "CONNECTION_STRING=$(escape_conn "$(build_conn_string printfarmer postgres "$stale_pw")")"
    } >> ".deploy-config"

    {
        echo "DEPLOYMENT_TYPE=monolithic"
        echo "DB_PROVIDER=postgres"
        echo "POSTGRES_DB=printfarmer"
        echo "POSTGRES_USER=postgres"
        echo "POSTGRES_PASSWORD=$active_pw"
        echo "CONNECTION_STRING=$(build_conn_string printfarmer postgres "$active_pw")"
    } > ".env"
    rm -f docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --redeploy --dry-run 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Redeploy output: $output"
    fi
    assert_equals "0" "$exit_code" "Redeploy with divergent legacy credentials should complete successfully" || true
    assert_contains "$output" "Detected legacy/divergent PostgreSQL credential storage" "Should log that a migration occurred" || true
    assert_not_contains "$output" "$active_pw" "Migration log must not print the active credential" || true
    assert_not_contains "$output" "$stale_pw" "Migration log must not print the stale credential" || true
    assert_file_has_exact_line ".env" "POSTGRES_PASSWORD=$active_pw" "Active .env credential must be preserved verbatim" || true
    assert_file_has_exact_line ".deploy-config" "POSTGRES_PASSWORD=$active_pw" "Config should be migrated to the active .env credential" || true
    assert_file_has_exact_line ".deploy-config" "DB_PASSWORD=$active_pw" "DB_PASSWORD alias should be resynced to the active credential" || true
    assert_not_contains "$(cat .deploy-config)" "$stale_pw" ".deploy-config must not retain the stale credential anywhere" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# ---------------------------------------------------------------------------
# Scenario 2: with no active .env, divergent POSTGRES_PASSWORD / DB_PASSWORD /
# embedded connection-string values must resolve to POSTGRES_PASSWORD.
# ---------------------------------------------------------------------------
test_divergent_legacy_values_resolve_to_postgres_password() {
    start_test "divergent legacy credentials resolve to POSTGRES_PASSWORD"

    cd "$TEST_TEMP_DIR"
    local canonical_pw="canonical-credential"
    local alias_pw="different-alias-credential"
    local conn_pw="yet-another-embedded-credential"
    write_base_postgres_config ".deploy-config"
    {
        echo "POSTGRES_PASSWORD=$canonical_pw"
        echo "DB_PASSWORD=$alias_pw"
        echo "CONNECTION_STRING=$(escape_conn "$(build_conn_string printfarmer postgres "$conn_pw")")"
    } >> ".deploy-config"
    rm -f ".env" docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --regenerate-config 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Regenerate output: $output"
    fi
    assert_equals "0" "$exit_code" "Regeneration with divergent legacy credentials should complete successfully" || true
    assert_contains "$output" "Detected legacy/divergent PostgreSQL credential storage" "Should log that a migration occurred" || true
    assert_file_has_exact_line ".deploy-config" "POSTGRES_PASSWORD=$canonical_pw" "POSTGRES_PASSWORD must remain the source of truth" || true
    assert_file_has_exact_line ".deploy-config" "DB_PASSWORD=$canonical_pw" "DB_PASSWORD alias should be resynced to POSTGRES_PASSWORD" || true
    assert_file_has_exact_line ".env" "POSTGRES_PASSWORD=$canonical_pw" "Generated .env should use the canonical password" || true
    assert_not_contains "$(cat .deploy-config)" "$alias_pw" "Divergent DB_PASSWORD value must not survive migration" || true
    assert_not_contains "$(cat .deploy-config)" "$conn_pw" "Divergent embedded connection-string credential must not survive migration" || true
    assert_not_contains "$(cat .env)" "$alias_pw" "Divergent DB_PASSWORD value must not leak into generated .env" || true
    assert_not_contains "$(cat .env)" "$conn_pw" "Divergent embedded connection-string credential must not leak into generated .env" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# ---------------------------------------------------------------------------
# Scenario 3: a config that never persisted the DB_PASSWORD compatibility
# alias must have it synchronized automatically.
# ---------------------------------------------------------------------------
test_missing_db_password_alias_is_synchronized() {
    start_test "missing DB_PASSWORD alias is synchronized from POSTGRES_PASSWORD"

    cd "$TEST_TEMP_DIR"
    local only_pw="only-postgres-credential-set"
    write_base_postgres_config ".deploy-config"
    echo "POSTGRES_PASSWORD=$only_pw" >> ".deploy-config"
    rm -f ".env" docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --regenerate-config 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Regenerate output: $output"
    fi
    assert_equals "0" "$exit_code" "Regeneration with a missing DB_PASSWORD alias should complete successfully" || true
    assert_file_has_exact_line ".deploy-config" "POSTGRES_PASSWORD=$only_pw" "POSTGRES_PASSWORD should be unchanged" || true
    assert_file_has_exact_line ".deploy-config" "DB_PASSWORD=$only_pw" "Missing DB_PASSWORD alias should be added" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# ---------------------------------------------------------------------------
# Scenario 4: repeated redeploys must be idempotent and never rotate an
# already-canonical credential.
# ---------------------------------------------------------------------------
test_redeploy_idempotent_no_credential_rotation() {
    start_test "repeated redeploys do not rotate credentials"

    cd "$TEST_TEMP_DIR"
    local stable_pw="stable-credential"
    write_base_postgres_config ".deploy-config"
    {
        echo "POSTGRES_PASSWORD=$stable_pw"
        echo "DB_PASSWORD=$stable_pw"
        echo "CONNECTION_STRING=$(escape_conn "$(build_conn_string printfarmer postgres "$stable_pw")")"
    } >> ".deploy-config"
    {
        echo "DEPLOYMENT_TYPE=monolithic"
        echo "DB_PROVIDER=postgres"
        echo "POSTGRES_DB=printfarmer"
        echo "POSTGRES_USER=postgres"
        echo "POSTGRES_PASSWORD=$stable_pw"
        echo "CONNECTION_STRING=$(build_conn_string printfarmer postgres "$stable_pw")"
    } > ".env"
    rm -f docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --redeploy --dry-run 2>&1"
    local exit_code_1
    exit_code_1=$(get_output_exit_code)
    local output_1
    output_1=$(get_output)

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --redeploy --dry-run 2>&1"
    local exit_code_2
    exit_code_2=$(get_output_exit_code)
    local output_2
    output_2=$(get_output)

    local failures_before=$TESTS_FAILED
    if [[ "$exit_code_1" -ne 0 ]]; then
        test_info "First redeploy output: $output_1"
    fi
    if [[ "$exit_code_2" -ne 0 ]]; then
        test_info "Second redeploy output: $output_2"
    fi
    assert_equals "0" "$exit_code_1" "First redeploy should complete successfully" || true
    assert_equals "0" "$exit_code_2" "Second redeploy should complete successfully" || true
    assert_not_contains "$output_1" "Detected legacy/divergent PostgreSQL credential storage" "Already-consistent credentials should not be flagged as divergent" || true
    assert_not_contains "$output_2" "Detected legacy/divergent PostgreSQL credential storage" "Second redeploy should still be a no-op migration" || true
    assert_file_has_exact_line ".deploy-config" "POSTGRES_PASSWORD=$stable_pw" "Password must remain unchanged after first redeploy" || true
    assert_file_has_exact_line ".env" "POSTGRES_PASSWORD=$stable_pw" "Env password must remain unchanged after repeated redeploys" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# ---------------------------------------------------------------------------
# Scenario 5: the SQL Server provider must never be touched by the
# PostgreSQL-specific migration logic, even with a divergent DB_PASSWORD alias.
# ---------------------------------------------------------------------------
test_sqlserver_provider_is_untouched_by_postgres_migration() {
    start_test "SQL Server provider is untouched by PostgreSQL credential migration"

    cd "$TEST_TEMP_DIR"
    local sql_pw="sqlserver-credential"
    local stale_alias_pw="stale-unrelated-alias"
    write_base_sqlserver_config ".deploy-config"
    {
        echo "SQLSERVER_PASSWORD=$sql_pw"
        echo "MSSQL_SA_PASSWORD=$sql_pw"
        echo "DB_PASSWORD=$stale_alias_pw"
    } >> ".deploy-config"
    rm -f ".env" docker-compose.yml docker-compose.override.yml

    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --regenerate-config 2>&1"
    local exit_code
    exit_code=$(get_output_exit_code)
    local output
    output=$(get_output)
    local failures_before=$TESTS_FAILED

    if [[ "$exit_code" -ne 0 ]]; then
        test_info "Regenerate output: $output"
    fi
    assert_equals "0" "$exit_code" "SQL Server regeneration should complete successfully" || true
    assert_not_contains "$output" "Detected legacy/divergent PostgreSQL credential storage" "SQL Server provider must never trigger PostgreSQL credential migration" || true
    assert_file_has_exact_line ".deploy-config" "SQLSERVER_PASSWORD=$sql_pw" "SQL Server SA password must be preserved" || true
    assert_file_has_exact_line ".env" "SQLSERVER_PASSWORD=$sql_pw" "SQL Server SA password must carry into .env unchanged" || true

    if [[ "$TESTS_FAILED" -eq "$failures_before" ]]; then
        pass_test
    fi
}

# Run all tests
run_all_tests() {
    setup

    test_redeploy_prefers_active_env_password_over_stale_config
    test_divergent_legacy_values_resolve_to_postgres_password
    test_missing_db_password_alias_is_synchronized
    test_redeploy_idempotent_no_credential_rotation
    test_sqlserver_provider_is_untouched_by_postgres_migration

    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Database Credential Migration Tests"
fi
