#!/bin/bash

# test-deploy-docker.sh - Tests for the main deployment script
# Tests argument parsing, configuration validation, and deployment logic

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"
COMPOSE_GENERATOR="$REPO_ROOT/scripts/docker/compose-generator.sh"
INSTALL_SCRIPT="$REPO_ROOT/install.sh"

# Source test framework
source "$SCRIPT_DIR/test-framework.sh"

# Test configuration
TEST_TEMP_DIR=""
ORIGINAL_PWD=""
TEARDOWN_COMPLETE=false

write_default_deploy_config() {
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
NETWORK_MODE=bridge
ENABLE_DISCOVERY=no
ALLOW_LOCAL_NETWORK=yes
NETWORK_RANGES=192.168.0.0/16
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
WEB_PORT=3000
ENVIRONMENT=Development
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=1
ENABLE_ORCA_WORKER=yes
ENABLE_SPOOLMAN=no
ORCASLICER_VERSION=2.4.0
USE_EXTERNAL_STORAGE=no
EOF
}

setup() {
    setup_test_environment
    TEST_TEMP_DIR=$(create_test_temp_dir)
    ORIGINAL_PWD=$(pwd)
    test_info "Using temp directory: $TEST_TEMP_DIR"
    trap teardown EXIT
    write_default_deploy_config
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

# Test help output
test_help_output() {
    start_test "deploy script help output"
    
    capture_output "$DEPLOY_SCRIPT --help"
    local output=$(get_output)
    
    assert_contains "$output" "PrintFarmer Docker Deployment Script" "Help should contain script title"
    assert_contains "$output" "USAGE:" "Help should contain usage section"
    assert_contains "$output" "--rebuild-orcaslicer" "Help should document the clean OrcaSlicer rebuild control"
    assert_contains "$output" "ORCA_FORCE_REBUILD=1" "Help should document the rebuild environment variable"
    assert_not_contains "$output" "--architecture" "Help should not mention removed architecture option"
    
    pass_test
}

# Test basic deploy script execution
test_basic_execution() {
    start_test "basic deploy script execution"
    
    # Deploy script should run successfully in dry-run mode
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    if [[ "$output" != *"Setup completed successfully"* ]]; then
        test_info "Dry-run output: $output"
    fi

    assert_contains "$output" "Setup completed successfully" "Deploy script should complete in dry-run mode"
    
    pass_test
}

# Test dry-run mode
test_dry_run_mode() {
    start_test "dry-run mode execution"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Dry-run should complete successfully"
    assert_contains "$output" "To deploy:" "Dry-run should show deployment command"
    
    pass_test
}

test_compose_helper_available_when_sourced() {
    start_test "compose helper available in verify-only execution path"

    local helper_script="$TEST_TEMP_DIR/check-compose-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
type dc >/dev/null
EOF
    chmod +x "$helper_script"

    assert_exit_code 0 "$helper_script" "Sourcing deploy-docker.sh should define the dc helper"

    pass_test
}

test_redeploy_cleanup_prunes_only_unused_images_and_build_cache() {
    start_test "redeploy cleanup prunes unused images and build cache"

    local helper_script="$TEST_TEMP_DIR/redeploy-cleanup-helper.sh"
    local docker_calls="$TEST_TEMP_DIR/redeploy-cleanup-docker-calls"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
DOCKER_CALLS="$docker_calls"
docker() {
    printf '%s\n' "\$*" >> "\$DOCKER_CALLS"
}

DRY_RUN=false
cleanup_redeploy_docker_artifacts
grep -Fxq "image prune --force" "\$DOCKER_CALLS"
grep -Fxq "builder prune --force" "\$DOCKER_CALLS"
! grep -q -- "--all" "\$DOCKER_CALLS"
! grep -q "volume" "\$DOCKER_CALLS"

: > "\$DOCKER_CALLS"
DRY_RUN=true
cleanup_redeploy_docker_artifacts
[[ ! -s "\$DOCKER_CALLS" ]]
EOF
    chmod +x "$helper_script"

    assert_exit_code 0 "$helper_script" \
        "Cleanup should prune unused images/cache, preserve volumes, and skip dry-runs"

    local redeploy_helper="$TEST_TEMP_DIR/redeploy-cleanup-failure-helper.sh"
    local redeploy_calls="$TEST_TEMP_DIR/redeploy-cleanup-failure-calls"
    local redeploy_warnings="$TEST_TEMP_DIR/redeploy-cleanup-failure-warnings"
    local redeploy_config="$TEST_TEMP_DIR/redeploy-cleanup-config"
    cat > "$redeploy_config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
NETWORK_MODE=bridge
COMPOSE_FILE=docker-compose.yml
EOF
    cat > "$redeploy_helper" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
CONFIG_FILE="$redeploy_config"
DRY_RUN=false
NO_CACHE=true
REDEPLOY_CALLS="$redeploy_calls"
REDEPLOY_WARNINGS="$redeploy_warnings"

print_header() { :; }
print_info() { :; }
print_success() { :; }
print_warning() { printf '%s\n' "\$*" >> "\$REDEPLOY_WARNINGS"; }
capture_config_overrides() { :; }
enforce_supported_orcaslicer_release() { :; }
restore_config_overrides() { :; }
normalize_worker_configuration() { return 0; }
migrate_legacy_db_credentials() { :; }
validate_configuration() { :; }
save_deployment_config() { :; }
generate_env_file() { :; }
generate_react_env_production() { :; }
generate_deployment_config() { return 0; }
prepare_external_storage_directories() { return 0; }
prepare_orcaslicer_worker_temp_directories() { return 0; }
prepare_pgadmin_setup() { return 0; }
validate_external_storage_permissions() { return 0; }
ensure_tls_certificates() { :; }
deploy_containers() {
    [[ "\$NO_CACHE" == "true" ]]
    printf '%s\n' containers-started >> "\$REDEPLOY_CALLS"
}
setup_initial_admin() { :; }
print_calibration_status_line() { :; }
docker() {
    printf '%s\n' "\$*" >> "\$REDEPLOY_CALLS"
    [[ "\$*" != "image prune --force" ]]
}

redeploy_existing
EOF
    chmod +x "$redeploy_helper"

    assert_exit_code 0 "cd '$TEST_TEMP_DIR' && '$redeploy_helper'" \
        "A cleanup failure should not fail an otherwise successful redeploy"
    assert_equals "containers-started" "$(sed -n '1p' "$redeploy_calls")" \
        "Redeploy should start rebuilt containers before cleanup begins"
    assert_equals "image prune --force" "$(sed -n '2p' "$redeploy_calls")" \
        "Image cleanup should begin only after rebuilt containers start"
    assert_file_has_exact_line "$redeploy_calls" "image prune --force" \
        "Redeploy call site should invoke image cleanup"
    assert_file_has_exact_line "$redeploy_calls" "builder prune --force" \
        "Cleanup should continue to builder pruning after an image prune failure"
    assert_contains "$(cat "$redeploy_warnings")" \
        "Redeployment completed, but some unused Docker artifacts could not be pruned" \
        "Redeploy call site should surface cleanup failure as a warning"

    pass_test
}

test_verification_uses_anonymous_api_endpoint() {
    start_test "deployment verification uses anonymous API endpoint"

    local script_content
    script_content=$(cat "$DEPLOY_SCRIPT")

    assert_contains "$script_content" "\$api_url/api/setup/status" "Verification should probe the anonymous setup endpoint"
    assert_not_contains "$script_content" "\$api_url/api/catalog/manufacturers" "Verification should not require catalog authorization"

    pass_test
}

# Test batch mode
test_batch_mode() {
    start_test "batch mode execution"
    
    capture_output "$(get_deploy_script_command --batch --dry-run)"
    local output=$(get_output)
    
    # Should not prompt for input in batch mode and should complete successfully
    assert_contains "$output" "Setup completed successfully" "Batch mode should complete successfully"
    assert_contains "$output" "Dry-run" "Should indicate dry-run mode"
    
    pass_test
}

# Test configuration file generation
test_config_file_generation() {
    start_test "configuration file generation"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Check that .env file is mentioned or created
    assert_contains "$output" ".env" "Should mention environment file creation"
    assert_file_exists "$TEST_TEMP_DIR/generated/src/Web/ReactApp/.env.production" \
        "React production environment should be generated under the explicit output directory"
    
    pass_test
}

# Test environment variable settings
test_environment_variables() {
    start_test "environment variable configuration"
    
    # Test basic deploy script execution using helper function
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should show microservices architecture"
    
    pass_test
}

test_api_deployable_health_statuses() {
    start_test "API deployment accepts healthy and degraded readiness states"

    local helper_script="$TEST_TEMP_DIR/api-health-status-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
api_health_status_is_deployable Healthy
api_health_status_is_deployable Degraded
if api_health_status_is_deployable Unhealthy; then
    exit 1
fi
if api_health_status_is_deployable ""; then
    exit 1
fi
EOF
    chmod +x "$helper_script"

    assert_exit_code 0 "$helper_script" "Only Healthy and Degraded API states should permit deployment"
    rm -f "$helper_script"

    pass_test
}

# Test no Redis configuration
test_no_redis_configuration() {
    start_test "no Redis configuration prompts"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Should not contain Redis-related prompts or configuration
    assert_not_contains "$output" "Redis" "Should not prompt for Redis configuration"
    assert_not_contains "$output" "persistent Redis" "Should not mention Redis persistence"
    
    pass_test
}

# Test no PrusaSlicer configuration
test_no_prusaslicer_configuration() {
    start_test "no PrusaSlicer configuration prompts"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Should not contain PrusaSlicer-related prompts or configuration
    assert_not_contains "$output" "PrusaSlicer" "Should not prompt for PrusaSlicer configuration"
    assert_not_contains "$output" "Prusa workers" "Should not mention Prusa workers"
    
    pass_test
}

# Test port validation
test_port_validation() {
    start_test "port validation and conflict detection"
    
    cd "$TEST_TEMP_DIR"
    
    # Test with custom ports using helper function
    export API_PORT="5555"
    export WEB_PORT="3333"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Should complete without port conflicts (assuming ports are free)
    assert_contains "$output" "Setup completed successfully" "Should complete with custom ports"
    
    unset API_PORT WEB_PORT
    
    pass_test
}

# Test deployment configuration output
test_deployment_config_output() {
    start_test "deployment configuration output"
    
    cd "$TEST_TEMP_DIR"
    
    # Deploy script should always use microservices architecture
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should indicate microservices deployment"
    assert_contains "$output" "Setup completed successfully" "Should complete successfully"
    
    pass_test
}

# Test worker configuration
test_worker_configuration() {
    start_test "worker configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Create configuration file for deploy script
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_DISTRIBUTED_SLICING=true
DB_PROVIDER=postgres
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Orca Workers: 2" "Should show configured Orca worker count"
    write_default_deploy_config
    
    pass_test
}

# Regression test for issue #1908: the OrcaSlicer worker's /app/temp bind mount must be
# pre-created with permissions the immutable, non-root worker container (UID 1001) can
# write to -- even when USE_EXTERNAL_STORAGE=no (the default), since
# docker-compose.orcaslicer-worker.yml always bind-mounts a host directory there.
# Without this, Docker auto-creates the host directory as root:root on first use, which
# shadows the appuser:appuser ownership baked into the image, causing every slice job to
# fail immediately with UnauthorizedAccessException.
test_orcaslicer_worker_temp_directory_permissions() {
    start_test "OrcaSlicer worker temp directory permissions (issue #1908)"
    
    cd "$TEST_TEMP_DIR"
    
    # USE_EXTERNAL_STORAGE is deliberately omitted/no here: the worker temp bind mount
    # is not gated behind that flag, unlike models/gcode/profiles.
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_DISTRIBUTED_SLICING=true
DB_PROVIDER=postgres
USE_EXTERNAL_STORAGE=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Pre-creating OrcaSlicer Worker Temp Directories" \
        "Should announce OrcaSlicer worker temp directory preparation"
    assert_dir_exists "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" \
        "OrcaSlicer worker temp directory should be pre-created"
    
    local perms
    perms=$(stat -c '%a' "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" 2>/dev/null || stat -f '%A' "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" 2>/dev/null || echo "unknown")
    assert_equals "777" "$perms" "OrcaSlicer worker temp directory should be world-writable (777) so appuser (container UID/GID 1001) can write per-job temp directories regardless of which host UID/GID owns the directory"
    
    write_default_deploy_config
    
    pass_test
}

# Regression test for issue #1908: an upgrade-in-place scenario. A deployment that hit
# the original bug can be left with the worker temp directory already existing with
# permissions that block the non-root appuser from writing to it (e.g. root:root from
# Docker auto-creating the bind mount). The fix must end up with 777 permissions either
# way -- whether that's a direct chmod (this test's same-owner case, where chmod succeeds
# regardless of the directory's current mode) or, when the deploy user does not own the
# directory (e.g. a real root:root leftover, which this harness cannot simulate without
# root/sudo), the rm -rf + recreate fallback in prepare_orcaslicer_worker_temp_directories().
# This test exercises the observable contract (pre-existing directory with wrong
# permissions ends up at 777) as a regression guard for the recovery behavior overall.
test_orcaslicer_worker_temp_directory_recreated_when_permissions_are_wrong() {
    start_test "OrcaSlicer worker temp directory recreated when pre-existing with bad permissions (issue #1908)"
    
    cd "$TEST_TEMP_DIR"
    
    # Simulate a directory left over from a prior broken deploy: it exists, but with
    # permissions that block the group/other write access appuser needs (000 here
    # stands in for "not writable by appuser", regardless of the exact original owner).
    mkdir -p "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp"
    chmod 000 "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp"
    
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_DISTRIBUTED_SLICING=true
DB_PROVIDER=postgres
USE_EXTERNAL_STORAGE=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_dir_exists "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" \
        "OrcaSlicer worker temp directory should still exist after recovery"
    
    local perms
    perms=$(stat -c '%a' "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" 2>/dev/null || stat -f '%A' "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" 2>/dev/null || echo "unknown")
    assert_equals "777" "$perms" "A pre-existing directory with unwritable permissions should be recreated with 777 so appuser can write again regardless of host UID/GID"
    
    write_default_deploy_config
    
    pass_test
}

# Regression test for issue #1908: when the worker is disabled, no temp directory
# preparation should occur (no unnecessary directory creation / noise).
test_orcaslicer_worker_temp_directory_skipped_when_disabled() {
    start_test "OrcaSlicer worker temp directory skipped when worker disabled (issue #1908)"
    
    cd "$TEST_TEMP_DIR"
    
    # Remove any worker temp directory left behind by an earlier test in this suite run
    # so this test genuinely proves nothing gets (re-)created, not just that it's already there.
    rm -rf "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp"
    
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=no
ENABLE_DISTRIBUTED_SLICING=false
DB_PROVIDER=postgres
USE_EXTERNAL_STORAGE=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_not_contains "$output" "Pre-creating OrcaSlicer Worker Temp Directories" \
        "Should not prepare worker temp directories when the worker is disabled"
    if [ -d "$TEST_TEMP_DIR/.volumes/printfarmer-orcaslicer-temp" ]; then
        fail_test "OrcaSlicer worker temp directory should not be created when the worker is disabled"
    fi
    
    write_default_deploy_config
    
    pass_test
}

# Test network configuration
test_network_configuration() {
    start_test "network configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Create configuration file for deploy script
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
NETWORK_MODE=bridge
DISCOVERY_RANGES=192.168.1.0/24,10.0.0.0/8
    DB_PROVIDER=postgres
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Network Discovery Configuration" "Should mention discovery configuration section"
    write_default_deploy_config
    
    pass_test
}

# Test database provider configuration
test_database_configuration() {
    start_test "database provider configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Test PostgreSQL
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
EOF
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local postgres_output=$(get_output)
    assert_contains "$postgres_output" "postgres" "Should configure PostgreSQL"
    
    # Test SQL Server
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=sqlserver
EOF
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local sqlserver_output=$(get_output)
    assert_contains "$sqlserver_output" "sqlserver" "Should configure SQL Server"
    write_default_deploy_config
    
    pass_test
}

# Test that generated DB password is included and propagated into ConnectionStrings__Default
test_generated_db_password_propagation() {
    start_test "generated DB password propagation"

    cd "$TEST_TEMP_DIR"

    # Run deploy script in dry-run batch mode to generate env file
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    # Determine expected env file
    local env_file="$TEST_TEMP_DIR/.env"

    assert_file_exists "$env_file" "Expected generated env file $env_file"

    local pg_pw
    pg_pw=$(grep -E '^POSTGRES_PASSWORD=' "$env_file" | tail -1 | cut -d= -f2- || true)
    local conn
    conn=$(grep -E '^ConnectionStrings__Default=' "$env_file" | tail -1 | cut -d= -f2- || true)

    assert_not_equals "" "$pg_pw" "POSTGRES_PASSWORD should be generated and present"
    assert_not_equals "" "$conn" "ConnectionStrings__Default should be present"

    # Verify password is included in connection string
    if [[ "$conn" != *"$pg_pw"* ]]; then
        fail_test "Connection string does not contain generated POSTGRES_PASSWORD"
    else
        pass_test
    fi
}

# Test SQL Server password propagation
test_generated_sqlserver_password_propagation() {
    start_test "generated SQL Server password propagation"

    cd "$TEST_TEMP_DIR"

    # Run deploy script in dry-run batch mode selecting sqlserver
    capture_output "$(get_deploy_script_command --dry-run --batch --env DB_PROVIDER=sqlserver)"
    local output=$(get_output)

    local env_file="$TEST_TEMP_DIR/.env"
    assert_file_exists "$env_file" "Expected generated env file $env_file"

    local sql_pw
    sql_pw=$(grep -E '^SQLSERVER_PASSWORD=' "$env_file" | tail -1 | cut -d= -f2- || true)
    local conn
    conn=$(grep -E '^ConnectionStrings__Default=' "$env_file" | tail -1 | cut -d= -f2- || true)

    assert_not_equals "" "$sql_pw" "SQLSERVER_PASSWORD should be generated and present"
    assert_not_equals "" "$conn" "ConnectionStrings__Default should be present"

    # For SQL Server the connection string typically includes 'Password=' value
    if ! echo "$conn" | grep -qi "Password="$sql_pw""; then
        # case-insensitive best-effort: check presence of password substring
        if [[ "$conn" != *"$sql_pw"* ]]; then
            fail_test "Connection string does not contain generated SQLSERVER_PASSWORD"
            return
        fi
    fi

    pass_test
}

# Test all database providers
test_all_database_combinations() {
    start_test "all database provider combinations"
    
    cd "$TEST_TEMP_DIR"
    
    local databases=("postgres" "sqlserver")
    
    for db in "${databases[@]}"; do
        # Create config file for this combination
        cat > "$TEST_TEMP_DIR/.deploy-config" << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$db
EOF
        
        capture_output "$(get_deploy_script_command --dry-run --batch)"
        local output=$(get_output)
        
        assert_contains "$output" "$db" "Should configure $db database"
        assert_contains "$output" "microservices" "Should show microservices architecture with $db database"
    done
    write_default_deploy_config
    
    pass_test
}

# Test addon configurations
test_addon_configurations() {
    start_test "addon stack configurations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test using command line arguments instead of environment variables
    capture_output "$(get_deploy_script_command --include-monitoring --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should complete with addon enabled"
    
    pass_test
}

# Test comprehensive deployment combinations
test_comprehensive_deployment_combinations() {
    start_test "comprehensive deployment combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test maximum configuration using config file
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_SPOOLMAN=yes
EOF
    
    capture_output "$(get_deploy_script_command --include-monitoring --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should configure microservices architecture"
    assert_contains "$output" "postgres" "Should configure PostgreSQL database"
    assert_contains "$output" "Setup completed successfully" "Should complete full configuration"
    
    # Test minimal configuration using config file
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
    DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=no
ENABLE_SPOOLMAN=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should configure microservices architecture"
    assert_contains "$output" "Setup completed successfully" "Should complete minimal configuration"
    write_default_deploy_config
    
    unset ARCHITECTURE DB_PROVIDER ENABLE_DISTRIBUTED_SLICING ENABLE_ORCA_WORKER ENABLE_SPOOLMAN
    
    pass_test
}

# Test configuration persistence
test_configuration_persistence() {
    start_test "configuration persistence"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should save configuration"
    
    pass_test
}

# Test validation logic
test_validation_logic() {
    start_test "configuration validation logic"
    
    cd "$TEST_TEMP_DIR"
    
    # Test basic validation by running deploy script
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should perform validation and complete"
    
    pass_test
}

# Test multistage build integration
test_multistage_build_integration() {
    start_test "multistage build integration"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Should complete successfully (multistage builds are internal implementation)
    assert_contains "$output" "Setup completed successfully" "Should complete with multistage builds"
    
    pass_test
}

# End-to-end regression: ensure deploy script generates provider-only .env for SQL Server
test_env_provider_only_end_to_end() {
    start_test "deploy script generates provider-only .env for sqlserver"

    cd "$TEST_TEMP_DIR"

    # Create a minimal deploy-config forcing microservices + sqlserver
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=sqlserver
EOF

    # Run deploy in dry-run, batch mode so it generates files but doesn't start containers
    # Use --config-file to explicitly point to the temp directory's config
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    # The script should mention environment file creation
    assert_contains "$output" ".env" "Should mention .env creation"

    # Expect the generated env file for microservices
    assert_file_exists ".env" "Should have created .env"

    # Inspect contents
    local env_content
    env_content=$(cat .env)

    # Must include MSSQL canonical variable and SQLSERVER entries
    assert_contains "$env_content" "MSSQL_SA_PASSWORD" "Env file should include MSSQL_SA_PASSWORD"
    assert_contains "$env_content" "SQLSERVER_PASSWORD" "Env file should include SQLSERVER_PASSWORD"

    # Must NOT include other providers' passwords
    assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env file should not include POSTGRES_PASSWORD when sqlserver selected"
    assert_not_contains "$env_content" "MYSQL_PASSWORD" "Env file should not include MYSQL_PASSWORD when sqlserver selected"

    # Must include ConnectionStrings__Default and a sqlserver server indicator
    assert_contains "$env_content" "ConnectionStrings__Default" "Env file should include ConnectionStrings__Default"
    assert_contains "$env_content" "Server=sqlserver" "Connection string should reference sqlserver host"

    # Also ensure the deploy script printed a masked summary for SQL Server credentials
    assert_contains "$output" "SQL Server credentials included (masked):" "Deploy output should include masked SQL Server credentials header"
    assert_contains "$output" "MSSQL_SA_PASSWORD" "Deploy output should print masked MSSQL_SA_PASSWORD"

    # Clean up
    rm -f .deploy-config .env .env || true

    pass_test
}

# End-to-end: postgres provider-only env generation and masked summary
test_env_provider_only_end_to_end_postgres() {
    start_test "deploy script generates provider-only .env for postgres"

    cd "$TEST_TEMP_DIR"

    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    assert_file_exists ".env" "Should have created .env for postgres"
    local env_content
    env_content=$(cat .env)

    assert_contains "$env_content" "POSTGRES_PASSWORD" "Env file should include POSTGRES_PASSWORD"
    assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env file should not include MSSQL_SA_PASSWORD when postgres selected"
    assert_not_contains "$env_content" "MYSQL_PASSWORD" "Env file should not include MYSQL_PASSWORD when postgres selected"
    assert_contains "$env_content" "ConnectionStrings__Default" "Env file should include ConnectionStrings__Default"

    # Masked summary in output
    assert_contains "$output" "PostgreSQL credentials included (masked):" "Deploy output should include masked Postgres credentials header"
    assert_contains "$output" "POSTGRES_PASSWORD" "Deploy output should print masked POSTGRES_PASSWORD"

    rm -f .deploy-config .env .env || true

    pass_test
}

# End-to-end: unsupported providers fail before writing deployment secrets
test_unsupported_mysql_provider() {
    start_test "deploy script rejects unsupported mysql provider"

    cd "$TEST_TEMP_DIR"

    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=mysql
EOF

    rm -f .env
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    assert_contains "$output" "Unsupported database provider 'mysql'" "Should reject MySQL before generation"
    if [[ -f .env ]]; then
        test_info "Unsupported-provider output: $output"
    fi

    assert_file_not_exists ".env" "Should not write deployment secrets for an unsupported provider"

    rm -f .deploy-config .env || true

    pass_test
}

# End-to-end: standard provider-only env generation for all providers
test_env_provider_standard_providers() {
    start_test "deploy script provider-only env generation (standard)"

    cd "$TEST_TEMP_DIR"

    local providers=("postgres" "sqlserver")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$provider
EOF

        capture_output "$(get_deploy_script_command --dry-run --batch)"
        local output=$(get_output)

        # Expect .env
        assert_file_exists ".env" "Should have created .env for $provider"
        local env_content
        env_content=$(cat .env)

        case "$provider" in
            postgres)
                assert_contains "$env_content" "POSTGRES_PASSWORD" "Env should include POSTGRES_PASSWORD for postgres"
                assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should not include MSSQL_SA_PASSWORD for postgres"
                assert_contains "$output" "PostgreSQL credentials included (masked):" "Output should include masked Postgres header"
                ;;
            sqlserver)
                assert_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should include MSSQL_SA_PASSWORD for sqlserver"
                assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env should not include POSTGRES_PASSWORD for sqlserver"
                assert_contains "$output" "SQL Server credentials included (masked):" "Output should include masked SQL Server header"
                ;;
        esac

        rm -f .deploy-config .env .env || true
    done

    pass_test
}

# End-to-end: microservices provider-only env generation for providers
test_env_provider_microservices_providers() {
    start_test "deploy script (microservices) provider-only env generation"

    cd "$TEST_TEMP_DIR"

    local providers=("postgres" "sqlserver")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$provider
EOF

        capture_output "$(get_deploy_script_command --dry-run --batch)"
        local output=$(get_output)

        # Microservices uses .env
        assert_file_exists ".env" "Should have created .env for microservices + $provider"
        local env_content
        env_content=$(cat .env)

        case "$provider" in
            postgres)
                assert_contains "$env_content" "POSTGRES_PASSWORD" "Env should include POSTGRES_PASSWORD for microservices+postgres"
                assert_contains "$output" "PostgreSQL credentials included (masked):" "Output should include masked Postgres header"
                ;;
            sqlserver)
                assert_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should include MSSQL_SA_PASSWORD for microservices+sqlserver"
                assert_contains "$output" "SQL Server credentials included (masked):" "Output should include masked SQL Server header"
                ;;
        esac

        rm -f .deploy-config .env || true
    done

    pass_test
}

# Test: password_not_logged_to_stdout (PHASE 1 - HIGH PRIORITY)
test_password_not_logged_to_stdout() {
    start_test "password not logged to stdout"
    
    cd "$TEST_TEMP_DIR"
    
    # Create config for postgres
    cat > .deploy-config << EOF
ARCHITECTURE=microservices
DB_PROVIDER=postgres
EOF
    
    # Run deployment script and capture stdout
    local output
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    output=$(get_output)
    
    # Check that env file was created
    assert_file_exists ".env" "Should create .env"
    
    # Get the actual password from env file
    local env_content
    env_content=$(cat .env)
    local actual_password
    actual_password=$(echo "$env_content" | grep "POSTGRES_PASSWORD=" | cut -d= -f2 | head -1 || echo "")
    
    # Verify password is generated
    assert_not_equals "" "$actual_password" "Password should be generated"
    
    # The plain password should NOT appear in stdout (security risk)
    # It should only appear in the .env file
    if [ -n "$actual_password" ]; then
        assert_not_contains "$output" "$actual_password" "Plain password should NOT be logged to stdout"
    fi
    
    # Verify masked version appears in output instead
    assert_contains "$output" "***" "Output should show masked password indicator"
    
    pass_test
}

# Test that .env files can be sourced without bash syntax errors (connection strings must be quoted)
test_env_file_sourcing_with_connection_strings() {
    start_test ".env file sourcing with connection strings"
    
    local test_env="$TEST_TEMP_DIR/test.env"
    
    # Create a test .env file with connection strings containing spaces (like SQL Server)
    cat > "$test_env" << 'EOF'
DEPLOYMENT_TYPE=microservices
DB_PROVIDER=sqlserver
SQLSERVER_PASSWORD=L0rWItvZR9KLaoYl!
MSSQL_SA_PASSWORD=L0rWItvZR9KLaoYl!
ConnectionStrings__SqlServer="Server=sqlserver;Database=printfarmer;User Id=sa;Password=L0rWItvZR9KLaoYl!;TrustServerCertificate=True;"
ConnectionStrings__Default="Server=localhost;Database=printfarmer;User Id=sa;Password=L0rWItvZR9KLaoYl!;TrustServerCertificate=True;"
CORS__AllowedOrigins=http://localhost:3000,http://localhost:8080
EOF
    
    # Test that the .env file can be sourced without bash errors
    cat > "$TEST_TEMP_DIR/test_source.sh" << 'EOFTEST'
#!/bin/bash
set -euo pipefail
set -a
source "$1"
set +a
echo "SOURCE_SUCCESS=true"
echo "MSSQL_SA_PASSWORD=$MSSQL_SA_PASSWORD"
echo "ConnectionStrings__Default=$ConnectionStrings__Default"
EOFTEST
    chmod +x "$TEST_TEMP_DIR/test_source.sh"
    
    # Run the source test
    capture_output "$TEST_TEMP_DIR/test_source.sh $test_env 2>&1"
    local output=$(get_output)
    
    # Verify no "command not found" errors (which would indicate unquoted connection strings)
    assert_not_contains "$output" "User: command not found" "Connection string should be quoted to avoid 'User: command not found' error"
    assert_not_contains "$output" "command not found" "Should not have bash command parsing errors"
    
    # Verify the variables were sourced correctly
    assert_contains "$output" "SOURCE_SUCCESS=true" "Source operation should succeed"
    assert_contains "$output" "MSSQL_SA_PASSWORD=L0rWItvZR9KLaoYl!" "MSSQL_SA_PASSWORD should be sourced correctly"
    assert_contains "$output" "ConnectionStrings__Default=" "ConnectionStrings__Default should be sourced"
    
    pass_test
}

# Test: PFARM__Spoolman__BaseUrl is written to .env when Spoolman is enabled
test_pfarm_spoolman_baseurl_in_env() {
    start_test "PFARM__Spoolman__BaseUrl written to .env when Spoolman enabled"

    cd "$TEST_TEMP_DIR"

    # Create config with Spoolman enabled AND configured with a URL
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_SPOOLMAN=yes
SPOOLMAN_BASE_URL=http://spoolman.local:7912
EOF

    # Run deploy script in dry-run, batch mode
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    # Check output for errors or completion
    if echo "$output" | grep -q "error\|Error\|ERROR" && ! echo "$output" | grep -q "completed"; then
        test_info "Deploy script output: $output"
    fi

    # Determine expected env file - .env is used
    local env_file=".env"

    if [ -z "$env_file" ] || [ ! -f "$env_file" ]; then
        test_info "Available files in TEST_TEMP_DIR: $(ls -la "$TEST_TEMP_DIR"/.env* 2>/dev/null || echo 'none')"
        fail_test "Could not find generated .env file"
        return
    fi
    
    local env_content
    env_content=$(cat "$env_file")

    # CRITICAL: Verify PFARM__Spoolman__BaseUrl is in the env file (this is the fix we added)
    # This variable should be present for the API to read Spoolman configuration
    if echo "$env_content" | grep -q "PFARM__Spoolman__BaseUrl"; then
        pass_test
    else
        test_info "Env file location: $env_file"
        test_info "Env file content:\n$env_content"
        fail_test "PFARM__Spoolman__BaseUrl not found in env file - the fix to deploy-docker.sh may not be working"
    fi

    # Clean up
    rm -f .deploy-config "$env_file" 2>/dev/null || true

}

# Test: PFARM__NetworkDiscovery__EnableDiscovery is written to .env when discovery is enabled
test_pfarm_network_discovery_enable_in_env() {
    start_test "PFARM__NetworkDiscovery__EnableDiscovery written to .env"

    cd "$TEST_TEMP_DIR"

    # Create config with discovery enabled
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_DISCOVERY=yes
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    assert_file_exists "$env_file" "Should create env file with discovery config"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify PFARM__NetworkDiscovery__EnableDiscovery is in the env file
    assert_contains "$env_content" "PFARM__NetworkDiscovery__EnableDiscovery=" "PFARM__NetworkDiscovery__EnableDiscovery should be in env file"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test: sync_env_var_with_file should resync ConnectionStrings__Default when shell export is stale
test_connection_string_sync_resolves_stale_export() {
    start_test "ConnectionStrings__Default resync updates shell export"

    cd "$TEST_TEMP_DIR"

    local env_file="$TEST_TEMP_DIR/sync.env"
    cat > "$env_file" << 'EOF'
ConnectionStrings__Default=Host=postgres;Database=printfarmer;Username=postgres;Password=SyncSecret123!
EOF

    local helper_script="$TEST_TEMP_DIR/sync-helper.sh"
    local stale_conn="Host=postgres;Database=printfarmer"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
ENV_FILE="$env_file"
export ConnectionStrings__Default="$stale_conn"
sync_env_var_with_file "ConnectionStrings__Default"
printf 'UPDATED=%s\n' "\$ConnectionStrings__Default"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output=$(get_output)

    local expected="Host=postgres;Database=printfarmer;Username=postgres;Password=SyncSecret123!"
    assert_contains "$output" "Resyncing ConnectionStrings__Default" "Should log resync when shell export is stale"

    local updated_line
    updated_line=$(echo "$output" | grep '^UPDATED=' | tail -1 || true)
    assert_contains "$updated_line" "$expected" "Shell export should match env file after resync"

    local file_value
    file_value=$(grep '^ConnectionStrings__Default=' "$env_file" | cut -d= -f2-)
    assert_equals "$expected" "$file_value" "Env file value should remain unchanged"

    rm -f "$helper_script" "$env_file" 2>/dev/null || true

    pass_test
}

# Test: sync_env_var_with_file should be a no-op when values already match
test_connection_string_sync_noop_when_values_match() {
    start_test "ConnectionStrings__Default sync no-op when already in sync"

    cd "$TEST_TEMP_DIR"

    local env_file="$TEST_TEMP_DIR/sync-noop.env"
    local expected="Host=postgres;Database=printfarmer;Username=postgres;Password=AlreadyThere!"
    cat > "$env_file" << EOF
ConnectionStrings__Default=$expected
EOF

    local helper_script="$TEST_TEMP_DIR/sync-noop-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
ENV_FILE="$env_file"
export ConnectionStrings__Default="$expected"
sync_env_var_with_file "ConnectionStrings__Default"
printf 'UPDATED=%s\n' "\$ConnectionStrings__Default"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output=$(get_output)

    local updated_line
    updated_line=$(echo "$output" | grep '^UPDATED=' | tail -1 || true)
    assert_contains "$updated_line" "$expected" "Shell export should stay unchanged when already in sync"
    # When values match, helper should not print resync message
    assert_not_contains "$output" "Resyncing ConnectionStrings__Default" "No resync log expected when values already match"

    rm -f "$helper_script" "$env_file" 2>/dev/null || true

    pass_test
}

# Test: stop_compose_services discovers and stops scaled orcaslicer-worker-N
# services (issue #1847) using a positional `ps` filter rather than
# `grep -E "$svc"`, so double-digit worker names (e.g. "orcaslicer-worker-10")
# are never substring-matched by single-digit names (e.g. "orcaslicer-worker-1").
test_stop_compose_services_scaled_workers_no_substring_match() {
    start_test "stop_compose_services handles scaled workers without substring collisions"

    cd "$TEST_TEMP_DIR"

    local compose_file="$TEST_TEMP_DIR/teardown-compose.yml"
    : > "$compose_file"

    local call_log="$TEST_TEMP_DIR/docker-calls.log"
    : > "$call_log"

    local helper_script="$TEST_TEMP_DIR/teardown-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# Mock 'docker' so no real containers are touched. Every invocation is
# appended to CALL_LOG for later assertions.
docker() {
    printf '%s\n' "\$*" >> "$call_log"

    if [ "\$1" = "compose" ]; then
        shift
        # Skip past -f/--env-file flags AND a spurious empty-string arg
        # (docker compose "\${env_arg[@]:-}" injects one when env_arg is an
        # empty array - a pre-existing bash empty-array `:-` fallback quirk,
        # not something introduced by this fix) to find the subcommand.
        while [ \$# -gt 0 ]; do
            case "\$1" in
                '') shift ;;
                -f|--env-file) shift 2 ;;
                *) break ;;
            esac
        done
        local subcmd="\$1"; shift || true

        case "\$subcmd" in
            ps)
                # Distinguish '--services' (discovery) from '--format ... [svc]' (status check)
                if [ "\${1:-}" = "--services" ]; then
                    printf '%s\n' frontend api orcaslicer-worker-1 orcaslicer-worker-2 orcaslicer-worker-10
                elif [ "\${1:-}" = "--quiet" ]; then
                    : # no container ids
                else
                    # --format '{{.Name}} {{.State}}' <svc> : report already-stopped
                    # (empty output) for every service so the retry loop exits
                    # immediately instead of sleeping.
                    :
                fi
                ;;
            stop|down|rm) : ;;
            *) : ;;
        esac
    fi
    return 0
}

source "$DEPLOY_SCRIPT"
stop_compose_services "" "$compose_file"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output=$(get_output)

    # Both a single-digit and a double-digit scaled worker must be discovered
    # and individually reported as stopped.
    assert_contains "$output" "Stopping service: orcaslicer-worker-1" "Should discover and stop orcaslicer-worker-1"
    assert_contains "$output" "Stopping service: orcaslicer-worker-10" "Should discover and stop orcaslicer-worker-10 (double-digit)"
    assert_contains "$output" "Service orcaslicer-worker-1 stopped" "orcaslicer-worker-1 should report stopped"
    assert_contains "$output" "Service orcaslicer-worker-10 stopped" "orcaslicer-worker-10 should report stopped"

    local call_log_content
    call_log_content=$(cat "$call_log")

    # The status-check `ps --format ... <svc>` call must pass the exact
    # service name as a positional argument to docker compose itself, not
    # rely on piping unfiltered output through `grep -E`. Confirm both the
    # single- and double-digit service names appear as their own
    # standalone-argument invocations.
    assert_contains "$call_log_content" "ps --format {{.Name}} {{.State}} orcaslicer-worker-1" "ps --format should be called with orcaslicer-worker-1 as a positional filter"
    assert_contains "$call_log_content" "ps --format {{.Name}} {{.State}} orcaslicer-worker-10" "ps --format should be called with orcaslicer-worker-10 as a positional filter"

    rm -f "$helper_script" "$compose_file" "$call_log" 2>/dev/null || true

    pass_test
}

# Test: ensure_connection_string_password patches env file and shell export when password missing
test_ensure_connection_string_password_updates_env_and_shell() {
    start_test "ensure_connection_string_password patches ConnectionStrings__Default"

    cd "$TEST_TEMP_DIR"

    local env_file="$TEST_TEMP_DIR/conn-missing.env"
    cat > "$env_file" << 'EOF'
DB_PROVIDER=postgres
POSTGRES_PASSWORD=Sup3rSecret!
ConnectionStrings__Default=Host=postgres;Database=printfarmer;Username=postgres
EOF

    local helper_script="$TEST_TEMP_DIR/conn-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -euo pipefail
source "$DEPLOY_SCRIPT"
ENV_FILE="$env_file"
export DB_PROVIDER=postgres
export POSTGRES_PASSWORD="Sup3rSecret!"
export ConnectionStrings__Default="Host=postgres;Database=printfarmer;Username=postgres"
ensure_connection_string_password
printf 'UPDATED=%s\n' "\$ConnectionStrings__Default"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output=$(get_output)

    local updated_line
    updated_line=$(echo "$output" | grep '^UPDATED=' | tail -1 || true)
    assert_contains "$updated_line" "Password=Sup3rSecret!" "Shell export should gain password"

    local patched
    patched=$(grep '^ConnectionStrings__Default=' "$env_file" | cut -d= -f2-)
    assert_contains "$patched" "Password=Sup3rSecret!" "Env file should gain password"

    assert_contains "$output" "patched using provider credentials" "Should log that password was patched"

    rm -f "$helper_script" "$env_file" 2>/dev/null || true

    pass_test
}

# Test: PFARM__NetworkDiscovery__DiscoverySubnets is written to .env and maps from NETWORK_RANGES
test_pfarm_network_discovery_subnets_in_env() {
    start_test "PFARM__NetworkDiscovery__DiscoverySubnets maps from NETWORK_RANGES"

    cd "$TEST_TEMP_DIR"

    # Create config with specific discovery ranges
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_DISCOVERY=yes
NETWORK_RANGES=192.168.1.0/24,10.0.0.0/8,172.16.0.0/12
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    assert_file_exists "$env_file" "Should create env file with discovery subnets"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify PFARM__NetworkDiscovery__DiscoverySubnets is present and matches NETWORK_RANGES
    assert_contains "$env_content" "PFARM__NetworkDiscovery__DiscoverySubnets=" "PFARM__NetworkDiscovery__DiscoverySubnets should be present in env file"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test: All PFARM variables are present together in the same .env file
test_pfarm_variables_complete_set() {
    start_test "All PFARM variables present together in .env file"

    cd "$TEST_TEMP_DIR"

    # Create full config with both Spoolman and Discovery
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
NETWORK_MODE=bridge
ALLOW_LOCAL_NETWORK=true
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
USE_EXTERNAL_STORAGE=no
ENABLE_SPOOLMAN=yes
SPOOLMAN_OPTION=2
SPOOLMAN_BASE_URL=http://spoolman.local:7912
ENABLE_DISCOVERY=true
INCLUDE_DISCOVERY=true
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    assert_file_exists "$env_file" "Should create env file with full PFARM configuration"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify all three PFARM__ variables are present (these are the critical application settings)
    assert_contains "$env_content" "PFARM__Spoolman__BaseUrl=" "Should include PFARM__Spoolman__BaseUrl for API Spoolman integration"
    assert_contains "$env_content" "PFARM__NetworkDiscovery__EnableDiscovery=" "Should include PFARM__NetworkDiscovery__EnableDiscovery for API discovery feature"
    assert_contains "$env_content" "PFARM__NetworkDiscovery__DiscoverySubnets=" "Should include PFARM__NetworkDiscovery__DiscoverySubnets for API network discovery"

    # Also verify critical source variables that should be present
    assert_contains "$env_content" "SPOOLMAN_BASE_URL=" "Should include SPOOLMAN_BASE_URL for Docker compose"
    assert_contains "$env_content" "ALLOWED_NETWORK_RANGES=" "Should include ALLOWED_NETWORK_RANGES for Docker configuration"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test: PFARM variables can be sourced without bash errors
test_pfarm_variables_sourcing() {
    start_test "PFARM variables can be sourced without bash errors"

    cd "$TEST_TEMP_DIR"

    # Create config
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
NETWORK_MODE=bridge
ALLOW_LOCAL_NETWORK=true
HTTP_PORT=8080
HTTPS_PORT=0
SERVER_HOST=localhost
API_PORT=5245
ENVIRONMENT=Development
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
USE_EXTERNAL_STORAGE=no
ENABLE_SPOOLMAN=yes
SPOOLMAN_OPTION=2
SPOOLMAN_BASE_URL=http://spoolman.local:7912
ENABLE_DISCOVERY=true
INCLUDE_DISCOVERY=true
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    # Create a test script to source the .env file
    cat > "$TEST_TEMP_DIR/test_pfarm_source.sh" << 'EOFTEST'
#!/bin/bash
set -euo pipefail
set -a
source "$1"
set +a
echo "SOURCE_SUCCESS=true"
echo "PFARM__Spoolman__BaseUrl=$PFARM__Spoolman__BaseUrl"
echo "PFARM__NetworkDiscovery__EnableDiscovery=$PFARM__NetworkDiscovery__EnableDiscovery"
echo "PFARM__NetworkDiscovery__DiscoverySubnets=$PFARM__NetworkDiscovery__DiscoverySubnets"
EOFTEST
    chmod +x "$TEST_TEMP_DIR/test_pfarm_source.sh"

    # Run the source test
    capture_output "$TEST_TEMP_DIR/test_pfarm_source.sh $env_file 2>&1"
    local output=$(get_output)

    # Verify no bash syntax errors
    assert_not_contains "$output" "command not found" "PFARM variables should not cause bash parsing errors"
    assert_not_contains "$output" "syntax error" "PFARM variables should not cause bash syntax errors"

    # Verify the variables were sourced correctly
    if [[ "$output" != *"PFARM__Spoolman__BaseUrl=http://spoolman.local:7912"* ]]; then
        test_info "PFARM source output: $output"
    fi

    assert_contains "$output" "SOURCE_SUCCESS=true" "Source operation should succeed"
    assert_contains "$output" "PFARM__Spoolman__BaseUrl=http://spoolman.local:7912" "PFARM__Spoolman__BaseUrl should be sourced correctly"
    assert_contains "$output" "PFARM__NetworkDiscovery__EnableDiscovery=" "PFARM__NetworkDiscovery__EnableDiscovery should be sourced"

    # Clean up
    rm -f .deploy-config "$env_file" "$TEST_TEMP_DIR/test_pfarm_source.sh" || true

    pass_test
}

# Create a Docker stub that satisfies install.sh prerequisite and upgrade calls.
create_installer_docker_stub() {
    local mock_bin="$1"
    mkdir -p "$mock_bin"
    cat > "$mock_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "${1:-}" in
    --version)
        echo "Docker version 27.0.0, build test"
        ;;
    info)
        exit 0
        ;;
    compose)
        if [[ "${2:-}" == "version" ]]; then
            echo "Docker Compose version v2.29.0"
        fi
        ;;
esac
exit 0
EOF
    chmod +x "$mock_bin/docker"
}

create_failing_command_stubs() {
    local mock_bin="$1"
    shift
    local command_name
    mkdir -p "$mock_bin"
    for command_name in "$@"; do
        cat > "$mock_bin/$command_name" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
        chmod +x "$mock_bin/$command_name"
    done
}

# Test: install.sh generates and preserves the lite monolith shared key.
test_installer_lite_slicer_worker_key() {
    start_test "installer lite profile configures slicer worker authentication"

    local install_dir="$TEST_TEMP_DIR/installer-lite"
    local mock_bin="$TEST_TEMP_DIR/installer-bin"
    local expected_key="test-worker-shared-key-907"
    create_installer_docker_stub "$mock_bin"

    capture_output "PATH='$mock_bin:$PATH' WORKER_SHARED_API_KEY='$expected_key' '$INSTALL_SCRIPT' --non-interactive --profile lite --port 18907 --dir '$install_dir' --dry-run"
    local output
    output=$(get_output)

    assert_file_exists "$install_dir/.env" "Lite installer should create .env"
    assert_file_exists "$install_dir/docker-compose.yml" "Lite installer should create docker-compose.yml"
    assert_contains "$(cat "$install_dir/.env")" "WORKER_SHARED_API_KEY=$expected_key" "Lite installer should persist the shared key"
    assert_contains "$(cat "$install_dir/docker-compose.yml")" "WorkerAuth__SharedKey=\${WORKER_SHARED_API_KEY}" "Lite monolith should receive the shared key"
    assert_not_contains "$output" "$expected_key" "Installer output must not expose the shared key"
    local env_mode
    env_mode=$(stat -c '%a' "$install_dir/.env" 2>/dev/null || stat -f '%Lp' "$install_dir/.env")
    assert_equals "600" "$env_mode" "Generated .env should be readable only by its owner"

    capture_output "PATH='$mock_bin:$PATH' '$INSTALL_SCRIPT' --non-interactive --profile lite --port 18907 --dir '$install_dir' --reuse-config --dry-run"
    local preserved_key
    preserved_key=$(grep -m1 '^WORKER_SHARED_API_KEY=' "$install_dir/.env" | cut -d= -f2-)
    assert_equals "$expected_key" "$preserved_key" "Reinstall should preserve the shared key"

    pass_test
}

# Test: install.sh upgrades old lite installs without rotating the generated key.
test_installer_upgrade_adds_slicer_worker_key() {
    start_test "installer upgrade adds stable slicer worker authentication"

    local install_dir="$TEST_TEMP_DIR/installer-upgrade"
    local mock_bin="$TEST_TEMP_DIR/installer-upgrade-bin"
    mkdir -p "$install_dir"
    create_installer_docker_stub "$mock_bin"

    cat > "$install_dir/.env" <<'EOF'
IMAGE_TAG=latest
DEPLOY_PROFILE=lite
Jwt__Key=existing-jwt-key
EOF
    cat > "$install_dir/docker-compose.yml" <<'EOF'
services:
  printfarmer:
    container_name: printfarmer-monolith
    environment:
      - Jwt__Audience=${Jwt__Audience:-PrintFarmer}
EOF

    capture_output "PATH='$mock_bin:$PATH' '$INSTALL_SCRIPT' --upgrade --dir '$install_dir'"
    local output
    output=$(get_output)
    local generated_key
    generated_key=$(grep -m1 '^WORKER_SHARED_API_KEY=' "$install_dir/.env" | cut -d= -f2-)

    assert_not_equals "" "$generated_key" "Upgrade should generate a shared key for an old lite install"
    assert_equals "64" "${#generated_key}" "Upgrade should generate the requested 64-character shared key"
    assert_contains "$(cat "$install_dir/docker-compose.yml")" "WorkerAuth__SharedKey=\${WORKER_SHARED_API_KEY}" "Upgrade should wire the generated key into the monolith"
    assert_not_contains "$output" "$generated_key" "Upgrade output must not expose the generated key"

    capture_output "PATH='$mock_bin:$PATH' '$INSTALL_SCRIPT' --upgrade --dir '$install_dir'"
    local preserved_key
    preserved_key=$(grep -m1 '^WORKER_SHARED_API_KEY=' "$install_dir/.env" | cut -d= -f2-)
    local key_count
    key_count=$(grep -c '^WORKER_SHARED_API_KEY=' "$install_dir/.env")
    local mapping_count
    mapping_count=$(grep -c 'WorkerAuth__SharedKey=' "$install_dir/docker-compose.yml")

    assert_equals "$generated_key" "$preserved_key" "Repeated upgrades should not rotate the shared key"
    assert_equals "1" "$key_count" "Upgrade should keep one canonical shared-key entry"
    assert_equals "1" "$mapping_count" "Upgrade should keep one monolith key mapping"

    pass_test
}

# Test: initial .env generation replaces existing config atomically.
test_installer_env_write_is_atomic() {
    start_test "installer writes secret configuration atomically"

    local install_dir="$TEST_TEMP_DIR/installer-atomic"
    local mock_bin="$TEST_TEMP_DIR/installer-atomic-bin"
    local original_env
    mkdir -p "$install_dir"
    create_installer_docker_stub "$mock_bin"
    create_failing_command_stubs "$mock_bin" mv

    original_env=$'DEPLOY_PROFILE=lite\nJwt__Key=test-jwt\nWORKER_SHARED_API_KEY=test-worker-key'
    printf '%s\n' "$original_env" > "$install_dir/.env"

    local output_file="$TEST_TEMP_DIR/installer-atomic.out"
    local exit_code
    set +e
    PATH="$mock_bin:$PATH" "$INSTALL_SCRIPT" \
        --non-interactive \
        --profile lite \
        --port 18908 \
        --dir "$install_dir" \
        --reuse-config \
        --dry-run > "$output_file" 2>&1
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Installer should fail when atomic replacement fails"
    assert_equals "$original_env" "$(cat "$install_dir/.env")" "Failed replacement must not clobber the existing config"
    local temp_count
    temp_count=$(find "$install_dir" -maxdepth 1 -name '.env.tmp.*' -type f | wc -l | tr -d ' ')
    assert_equals "0" "$temp_count" "Failed replacement should clean the mode-600 temporary file"

    pass_test
}

# Test: secret generation aborts instead of returning predictable fallback data.
test_installer_fails_without_secure_entropy() {
    start_test "installer fails closed when secure entropy is unavailable"

    local install_dir="$TEST_TEMP_DIR/installer-entropy"
    local mock_bin="$TEST_TEMP_DIR/installer-entropy-bin"
    local original_env
    mkdir -p "$install_dir"
    create_installer_docker_stub "$mock_bin"
    create_failing_command_stubs "$mock_bin" openssl dd base64

    original_env=$'DEPLOY_PROFILE=lite\nJwt__Key=existing-test-jwt'
    printf '%s\n' "$original_env" > "$install_dir/.env"

    local output_file="$TEST_TEMP_DIR/installer-entropy.out"
    local exit_code
    set +e
    PATH="$mock_bin:$PATH" "$INSTALL_SCRIPT" \
        --non-interactive \
        --profile lite \
        --port 18909 \
        --dir "$install_dir" \
        --reuse-config \
        --dry-run > "$output_file" 2>&1
    exit_code=$?
    set -e

    assert_not_equals "0" "$exit_code" "Installer should fail when no CSPRNG succeeds"
    assert_contains "$(cat "$output_file")" "Unable to generate a secure secret" "Failure should explain the secure entropy requirement"
    assert_equals "$original_env" "$(cat "$install_dir/.env")" "Entropy failure must preserve the existing config"

    pass_test
}

assert_development_launcher_configures_worker_auth() {
    local script_content="$1"
    local label="$2"
    assert_contains "$script_content" "ASPNETCORE_ENVIRONMENT=Development" "$label should set the Development environment"
    assert_contains "$script_content" "lib_worker_auth.sh" "$label should source the secure worker-auth helper"
    assert_contains "$script_content" "ensure_worker_auth_shared_key" "$label should configure the canonical worker registration key"
    assert_not_contains "$script_content" "AllowInsecureDevelopmentRegistration" "$label must not bypass worker registration auth"
}

test_development_launchers_configure_worker_auth() {
    start_test "development API start paths configure fail-closed worker authentication"

    assert_development_launcher_configures_worker_auth "$(cat "$REPO_ROOT/scripts/pf-dev.sh")" "pf-dev.sh"
    assert_development_launcher_configures_worker_auth "$(cat "$REPO_ROOT/scripts/start-all-local.sh")" "start-all-local.sh"
    assert_development_launcher_configures_worker_auth "$(cat "$REPO_ROOT/scripts/start-all-local-with-workers.sh")" "start-all-local-with-workers.sh"

    pass_test
}

# Test: Slicer worker API key generation
test_slicer_worker_api_key_generation() {
    start_test "Slicer worker API key generation"

    cd "$TEST_TEMP_DIR"

    # Create config with OrcaSlicer workers enabled
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
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
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
USE_EXTERNAL_STORAGE=no
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_DISTRIBUTED_SLICING=true
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    assert_file_exists "$env_file" "Should create env file with API key configuration"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify one bootstrap key is generated without deprecated aliases.
    assert_contains "$env_content" "WORKER_SHARED_API_KEY=" "Should configure the API and workers with a bootstrap key"
    assert_not_contains "$env_content" "SlicerRegistry__ApiKey=" "Should not emit the removed registry-key alias"
    assert_not_contains "$env_content" "SLICER_WORKER_API_KEY" "Should not emit per-replica bootstrap keys"
    assert_not_contains "$env_content" "ORCA_WORKER_INSTANCE_ID=" "Scaled replicas must derive distinct runtime identities"
    assert_contains "$env_content" "DISCOVERY_SHARED_API_KEY=" "Should configure authenticated discovery event ingestion"

    local shared_key_line
    shared_key_line=$(grep "^WORKER_SHARED_API_KEY=" "$env_file" | head -1)
    local shared_key_value
    shared_key_value=$(echo "$shared_key_line" | cut -d'=' -f2-)
    local discovery_key_line
    discovery_key_line=$(grep "^DISCOVERY_SHARED_API_KEY=" "$env_file" | head -1)
    local discovery_key_value
    discovery_key_value=$(echo "$discovery_key_line" | cut -d'=' -f2-)
    
    assert_not_equals "$shared_key_value" "" "Bootstrap key value should not be empty"
    assert_not_contains "$output" "$shared_key_value" "Deployment output must not expose bootstrap key material"
    assert_not_equals "$discovery_key_value" "" "Discovery service key should not be empty"

    # A second run must recover the original bootstrap key before rewriting either file.
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local second_output
    second_output=$(get_output)
    local preserved_key_value
    preserved_key_value=$(grep "^WORKER_SHARED_API_KEY=" "$env_file" | head -1 | cut -d'=' -f2-)
    assert_equals "$shared_key_value" "$preserved_key_value" "Redeploy should preserve the worker bootstrap key"
    assert_not_contains "$second_output" "$preserved_key_value" "Redeploy output must not expose preserved bootstrap key material"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test: Slicer worker API key generation with single worker
test_slicer_worker_api_key_single_worker() {
    start_test "Slicer worker API key generation (single worker)"

    cd "$TEST_TEMP_DIR"

    # Create config with single OrcaSlicer worker
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.yml
DB_PROVIDER=postgres
CONNECTION_STRING=
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
ENABLE_SWAGGER=true
ENABLE_DETAILED_LOGGING=true
ENABLE_PGADMIN=false
DEVMODE_BYPASS_AUTH=false
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
INCLUDE_DISCOVERY=false
USE_EXTERNAL_STORAGE=no
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_DISTRIBUTED_SLICING=true
EOF

    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    local env_file=".env"

    assert_file_exists "$env_file" "Should create env file with API key configuration"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify the canonical bootstrap input is present, plus the stable per-process
    # instance identity that lets a single (non-scaled) worker upsert its existing
    # record across redeploys instead of accumulating a duplicate (issue #1528).
    assert_contains "$env_content" "WORKER_SHARED_API_KEY=" "Should configure the API and worker with a bootstrap key"
    assert_contains "$env_content" "ORCA_WORKER_INSTANCE_ID=orcaslicer-worker-1" "Single (non-scaled) workers must reuse a stable runtime identity across redeploys"
    assert_not_contains "$env_content" "SlicerRegistry__ApiKey=" "Should not emit the removed registry-key alias"

    local shared_key_line
    shared_key_line=$(grep "^WORKER_SHARED_API_KEY=" "$env_file" | head -1)
    local shared_key_value
    shared_key_value=$(echo "$shared_key_line" | cut -d'=' -f2-)
    
    assert_not_equals "$shared_key_value" "" "Bootstrap key value should not be empty"
    assert_not_contains "$output" "$shared_key_value" "Deployment output must not expose bootstrap key material"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test that PostgreSQL password authentication actually works (integration test)
test_postgres_password_authentication_integration() {
    start_test "PostgreSQL password authentication (integration test)"
    
    # Only run this test if Docker and the `docker compose` (v2 plugin)
    # subcommand are available. `skip_test` is not a defined function in
    # test-framework.sh, and CI runners (e.g. ubuntu-latest) ship the v2
    # `docker compose` plugin without the deprecated standalone
    # `docker-compose` binary, so checking for the latter always fails
    # there and previously aborted the whole suite with "command not
    # found" (exit 127) instead of skipping gracefully.
    if ! command -v docker &> /dev/null || ! docker compose version &> /dev/null; then
        test_warning "Skipping: Docker/Docker Compose not available"
        pass_test
        return 0
    fi
    
    cd "$TEST_TEMP_DIR"
    
    # The deploy script's --dry-run mode intentionally short-circuits compose
    # generation (compose-generator returns after show_dry_run). To validate
    # that the init-postgres.sh reference is wired into the compose the deploy
    # script would produce, invoke compose-generator directly against the test
    # temp dir. This preserves the original test intent (init script must be
    # referenced) without depending on --dry-run to leave files on disk.
    local generator="$REPO_ROOT/scripts/docker/compose-generator.sh"
    assert_file_exists "$generator" "compose-generator.sh should exist"
    capture_output "'$generator' --architecture microservices --db-provider postgres --output-dir '$TEST_TEMP_DIR' 2>&1 || true"

    # Also generate an env file via deploy dry-run so POSTGRES_PASSWORD is
    # produced through the same code path a real deployment would use.
    capture_output "$(get_deploy_script_command --dry-run --batch)"

    local compose_file="$TEST_TEMP_DIR/docker-compose.yml"
    assert_file_exists "$compose_file" "Generated compose file should exist"

    local env_file="$TEST_TEMP_DIR/.env"
    
    assert_file_exists "$env_file" "Generated env file should exist"
    
    # Extract password from env file
    local pg_pw
    pg_pw=$(grep -E '^POSTGRES_PASSWORD=' "$env_file" | tail -1 | cut -d= -f2- || true)
    
    assert_not_equals "" "$pg_pw" "POSTGRES_PASSWORD should be generated"
    
    # Verify that init-postgres.sh script is referenced in compose file
    if grep -q "init-postgres.sh" "$compose_file" 2>/dev/null; then
        pass_test "PostgreSQL init script is configured in compose file"
    else
        # Warning - not a failure, but good to know
        test_warning "PostgreSQL init script not found in compose file - password auth may not be enforced"
        pass_test
    fi
}

# Run all tests
run_all_tests() {
    setup
    
    test_help_output
    test_basic_execution
    test_dry_run_mode
    test_compose_helper_available_when_sourced
    test_redeploy_cleanup_prunes_only_unused_images_and_build_cache
    test_verification_uses_anonymous_api_endpoint
    test_batch_mode
    test_config_file_generation
    test_environment_variables
    test_api_deployable_health_statuses
    test_password_not_logged_to_stdout
    test_no_redis_configuration
    test_no_prusaslicer_configuration
    test_port_validation
    test_deployment_config_output
    test_worker_configuration
    test_orcaslicer_worker_temp_directory_permissions
    test_orcaslicer_worker_temp_directory_recreated_when_permissions_are_wrong
    test_orcaslicer_worker_temp_directory_skipped_when_disabled
    test_network_configuration  
    test_database_configuration
    test_all_database_combinations
    test_addon_configurations
    test_comprehensive_deployment_combinations
    test_configuration_persistence
    test_validation_logic
    test_multistage_build_integration
    test_env_provider_only_end_to_end
    test_env_provider_only_end_to_end_postgres
    test_unsupported_mysql_provider
    test_env_provider_standard_providers
    test_env_file_sourcing_with_connection_strings
    test_connection_string_sync_resolves_stale_export
    test_connection_string_sync_noop_when_values_match
    test_stop_compose_services_scaled_workers_no_substring_match
    test_ensure_connection_string_password_updates_env_and_shell
    test_pfarm_spoolman_baseurl_in_env
    test_pfarm_network_discovery_enable_in_env
    test_pfarm_network_discovery_subnets_in_env
    test_pfarm_variables_complete_set
    test_pfarm_variables_sourcing
    test_installer_lite_slicer_worker_key
    test_installer_upgrade_adds_slicer_worker_key
    test_installer_env_write_is_atomic
    test_installer_fails_without_secure_entropy
    test_development_launchers_configure_worker_auth
    test_slicer_worker_api_key_generation
    test_slicer_worker_api_key_single_worker
    test_postgres_password_authentication_integration
    test_pgadmin_flag_parsing
    test_pgadmin_config_persistence
    test_pgadmin_postgres_only
    test_orcaslicer_container_digest_resolved_from_registry_image
    test_orcaslicer_container_digest_empty_for_local_build_control
    test_orcaslicer_container_digest_rejects_malformed_operator_override
    test_orcaslicer_container_digest_second_call_refreshes_after_build
    test_orcaslicer_container_digest_operator_override_persists_across_second_call
    test_orcaslicer_container_digest_rejected_override_stays_rejected_on_second_call
    
    teardown
}

# Test pgAdmin flag parsing
test_pgadmin_flag_parsing() {
    start_test "pgAdmin flag parsing and handling"
    
    # Test that --enable-pgadmin flag is accepted
    assert_exit_code 0 "$DEPLOY_SCRIPT --help 2>&1 | grep -q 'enable-pgadmin' && echo 'ok'" "Should document --enable-pgadmin flag"
    
    pass_test
}

# Test pgAdmin config persistence
test_pgadmin_config_persistence() {
    start_test "pgAdmin configuration persistence in .deploy-config"
    
    # Create a test config with ENABLE_PGADMIN
    local test_config="$TEST_TEMP_DIR/test-pgadmin-config"
    cat > "$test_config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_PGADMIN=true
POSTGRES_DB=printfarmer
POSTGRES_USER=postgres
POSTGRES_PASSWORD=testpass123
EOF
    
    # Verify config contains ENABLE_PGADMIN
    assert_contains "$(cat $test_config)" "ENABLE_PGADMIN=true" "Config should have ENABLE_PGADMIN setting"
    
    pass_test
}

# Test pgAdmin only works with PostgreSQL
test_pgadmin_postgres_only() {
    start_test "pgAdmin PostgreSQL-only validation"
    
    # pgAdmin should only be supported with PostgreSQL
    # This is enforced in compose-generator.sh
    local compose_generator="$REPO_ROOT/scripts/docker/compose-generator.sh"
    assert_contains "$(grep -n 'postgres' "$compose_generator" | head -5)" "postgres" "compose-generator should reference PostgreSQL"
    
    pass_test
}

# Test: resolve_orcaslicer_container_digest resolves ORCASLICER_CONTAINER_DIGEST
# from the local worker image's repository digest when one is available (e.g.
# after pull-from-registry.sh / build-and-push-registry.sh) -- issue #2164.
# The mocked `docker image inspect` returns the full "repo@sha256:<hex>"
# reference (what Docker actually returns), but the assertion checks for the
# bare "sha256:<hex>" form, because that is the exact shape the API's
# WorkerClaimIdentity.IsContainerDigest() (src/slicer/Farm.Slicer.Module/Domain/
# WorkerClaimIdentity.cs) requires -- a repo-qualified reference would
# silently fail attestation even though this script reported it as resolved.
test_orcaslicer_container_digest_resolved_from_registry_image() {
    start_test "resolve_orcaslicer_container_digest resolves digest for registry-sourced image"

    cd "$TEST_TEMP_DIR"

    local repo_digest_reference="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    local expected_bare_digest="sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    local helper_script="$TEST_TEMP_DIR/digest-resolved-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# Mock a registry-sourced worker image: pull-from-registry.sh/build-and-push-registry.sh
# leave a resolvable RepoDigests entry on the local tag after tagging/pushing.
docker() {
    if [ "\$1" = "image" ] && [ "\$2" = "inspect" ]; then
        printf '%s\n' "$repo_digest_reference"
        return 0
    fi
    return 0
}

source "$DEPLOY_SCRIPT"

unset ORCASLICER_CONTAINER_DIGEST 2>/dev/null || true
ENABLE_ORCA_WORKER=yes
resolve_orcaslicer_container_digest
echo "DIGEST=[\$ORCASLICER_CONTAINER_DIGEST]"
echo "CALIBRATION=\$ORCASLICER_CALIBRATION_AVAILABLE"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "DIGEST=[$expected_bare_digest]" "Digest should be the bare sha256:<hex> form the API's IsContainerDigest() requires, not the full repo@sha256:<hex> reference"
    assert_contains "$output" "CALIBRATION=yes" "Calibration should be marked available when the digest resolves"
    assert_contains "$output" "Resolved OrcaSlicer worker container digest" "Should print a success message naming the resolved digest"

    rm -f "$helper_script" 2>/dev/null || true

    pass_test
}

# Test (control, paired with the test above): a pure local build -- e.g.
# build-orcaslicer-optimized.sh, which never pulls or pushes -- genuinely has
# no RepoDigests entry, so resolve_orcaslicer_container_digest must leave
# ORCASLICER_CONTAINER_DIGEST empty AND emit the operator-facing warning. This
# is required alongside the resolved-digest test above so the pair cannot pass
# by the resolver always (or never) setting the digest (issue #2164).
test_orcaslicer_container_digest_empty_for_local_build_control() {
    start_test "resolve_orcaslicer_container_digest stays empty and warns for a pure local build (control)"

    cd "$TEST_TEMP_DIR"

    local helper_script="$TEST_TEMP_DIR/digest-empty-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# Simulate build-orcaslicer-optimized.sh's pure local build: the image exists
# locally but was never pulled or pushed, so 'docker image inspect' has no
# RepoDigests entry to report.
docker() {
    if [ "\$1" = "image" ] && [ "\$2" = "inspect" ]; then
        return 1
    fi
    return 0
}

source "$DEPLOY_SCRIPT"

unset ORCASLICER_CONTAINER_DIGEST 2>/dev/null || true
ENABLE_ORCA_WORKER=yes
resolve_orcaslicer_container_digest
echo "DIGEST=[\$ORCASLICER_CONTAINER_DIGEST]"
echo "CALIBRATION=\$ORCASLICER_CALIBRATION_AVAILABLE"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "DIGEST=[]" "Digest must stay empty for a pure local build with no RepoDigests"
    assert_contains "$output" "CALIBRATION=no" "Calibration must be marked unavailable for a pure local build"
    assert_contains "$output" "Calibration generation will be unavailable" "Should print the operator-facing warning naming the consequence"
    assert_contains "$output" "pull-from-registry.sh or build-and-push-registry.sh" "Warning should name the scripts that enable calibration"

    rm -f "$helper_script" 2>/dev/null || true

    pass_test
}

# Test: resolve_orcaslicer_container_digest is called TWICE per real deploy
# (once from generate_env_file() before the worker image build/pull, again
# from deploy_containers() right after it completes). The second call must
# NOT mistake the first call's own resolved value for an operator override
# and freeze it in place -- it must re-query the (possibly now different)
# local image and pick up the fresh digest, closing the stale-attestation gap
# a prior round of review found in this two-call design (issue #2164).
test_orcaslicer_container_digest_second_call_refreshes_after_build() {
    start_test "resolve_orcaslicer_container_digest re-resolves on a second call instead of freezing the first result"

    cd "$TEST_TEMP_DIR"

    local digest_before="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:111111111111111111111111111111111111111111111111111111111111aaaa"
    local digest_after="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:222222222222222222222222222222222222222222222222222222222222bbbb"
    local bare_digest_before="sha256:111111111111111111111111111111111111111111111111111111111111aaaa"
    local bare_digest_after="sha256:222222222222222222222222222222222222222222222222222222222222bbbb"
    local call_count_file="$TEST_TEMP_DIR/inspect-call-count"
    local helper_script="$TEST_TEMP_DIR/digest-refresh-helper.sh"
    printf '0' > "$call_count_file"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# Simulate the local worker image changing between the two resolution calls
# (i.e. deploy_containers() built/pulled a new image after generate_env_file()
# ran): the first 'docker image inspect' call reports the pre-build digest,
# every subsequent call reports the post-build digest.
docker() {
    if [ "\$1" = "image" ] && [ "\$2" = "inspect" ]; then
        local count
        count=\$(cat "$call_count_file")
        count=\$((count + 1))
        printf '%s' "\$count" > "$call_count_file"
        if [ "\$count" -eq 1 ]; then
            printf '%s\n' "$digest_before"
        else
            printf '%s\n' "$digest_after"
        fi
        return 0
    fi
    return 0
}

source "$DEPLOY_SCRIPT"

unset ORCASLICER_CONTAINER_DIGEST 2>/dev/null || true
ENABLE_ORCA_WORKER=yes

# First call: mirrors generate_env_file(), before the (simulated) build.
resolve_orcaslicer_container_digest
echo "FIRST=[\$ORCASLICER_CONTAINER_DIGEST]"

# Second call: mirrors deploy_containers(), after the (simulated) build.
resolve_orcaslicer_container_digest
echo "SECOND=[\$ORCASLICER_CONTAINER_DIGEST]"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "FIRST=[$bare_digest_before]" "First call should resolve the pre-build digest"
    assert_contains "$output" "SECOND=[$bare_digest_after]" "Second call must re-query the image and pick up the new digest, not freeze the first call's value"

    rm -f "$helper_script" "$call_count_file" 2>/dev/null || true

    pass_test
}

# Test (paired with the refresh test above): when the FIRST call sees a valid
# operator-supplied override, that override must stick across a SECOND call
# too -- deploy_containers()'s re-resolution must not silently replace an
# explicit operator override with whatever the local image resolves to
# (issue #2164).
test_orcaslicer_container_digest_operator_override_persists_across_second_call() {
    start_test "resolve_orcaslicer_container_digest keeps honoring an operator override on a second call"

    cd "$TEST_TEMP_DIR"

    local operator_override="sha256:333333333333333333333333333333333333333333333333333333333333cccc"
    local image_digest="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:444444444444444444444444444444444444444444444444444444444444dddd"
    local helper_script="$TEST_TEMP_DIR/digest-override-persists-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# If the resolver ever fell through to actually querying docker after the
# override was accepted, this mock would report a DIFFERENT digest, letting
# the test catch that regression.
docker() {
    if [ "\$1" = "image" ] && [ "\$2" = "inspect" ]; then
        printf '%s\n' "$image_digest"
        return 0
    fi
    return 0
}

source "$DEPLOY_SCRIPT"

ORCASLICER_CONTAINER_DIGEST="$operator_override"
ENABLE_ORCA_WORKER=yes

resolve_orcaslicer_container_digest
echo "FIRST=[\$ORCASLICER_CONTAINER_DIGEST]"

resolve_orcaslicer_container_digest
echo "SECOND=[\$ORCASLICER_CONTAINER_DIGEST]"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "FIRST=[$operator_override]" "First call should honor the operator override"
    assert_contains "$output" "SECOND=[$operator_override]" "Second call must keep honoring the operator override, not replace it with the resolved image digest"

    rm -f "$helper_script" 2>/dev/null || true

    pass_test
}

# Test: an operator-supplied ORCASLICER_CONTAINER_DIGEST override that does not
# match the exact bare "sha256:<64 lowercase hex>" shape must be rejected, not
# trusted verbatim. This value is written into the unquoted .env heredoc /
# via update_kv_file, which load_env_file() later `source`s with `set -a` --
# an unvalidated value (e.g. containing a newline) could inject arbitrary
# additional configuration into that source (issue #2164 review finding).
test_orcaslicer_container_digest_rejects_malformed_operator_override() {
    start_test "resolve_orcaslicer_container_digest rejects a malformed operator-supplied override"

    cd "$TEST_TEMP_DIR"

    local malformed_override="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    local helper_script="$TEST_TEMP_DIR/digest-malformed-override-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

docker() {
    return 0
}

source "$DEPLOY_SCRIPT"

# Malformed: a full repo@sha256:<hex> reference is not the bare form the API
# requires, and also exercises the same shape-check used to defend against
# injection via an operator-supplied value.
ORCASLICER_CONTAINER_DIGEST="$malformed_override"
ENABLE_ORCA_WORKER=yes
resolve_orcaslicer_container_digest
echo "DIGEST=[\$ORCASLICER_CONTAINER_DIGEST]"
echo "CALIBRATION=\$ORCASLICER_CALIBRATION_AVAILABLE"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "DIGEST=[]" "A malformed operator override must be rejected, not written into .env verbatim"
    assert_contains "$output" "CALIBRATION=no" "Calibration must be marked unavailable when the override is rejected"
    assert_contains "$output" "malformed" "Should print a diagnostic explaining the override was rejected"

    rm -f "$helper_script" 2>/dev/null || true

    pass_test
}

# Test (round-3 review finding, Bishop/Hicks): a rejected malformed override
# must stay rejected on a SECOND call, not get silently "resurrected" by
# falling through to a fresh docker resolution. This is the sub-case that
# _ORCASLICER_DIGEST_SOURCE="override" being set BEFORE shape validation (not
# only on acceptance) exists to close -- without it, the first call would
# clear ORCASLICER_CONTAINER_DIGEST to empty on rejection, and the second
# call would see "empty" (not "already rejected"), proceed past the override
# check, and adopt whatever the local image resolves to -- silently
# overriding the operator's rejected intent (issue #2164).
test_orcaslicer_container_digest_rejected_override_stays_rejected_on_second_call() {
    start_test "resolve_orcaslicer_container_digest keeps a rejected malformed override rejected on a second call"

    cd "$TEST_TEMP_DIR"

    local malformed_override="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:d12fb8c8eac1aecd2dfb6377acd48f994f8fa439ed5292fa532dd82880f029fd"
    local image_digest="ghcr.io/olyforge3d/printfarmer-orcaslicer-worker@sha256:555555555555555555555555555555555555555555555555555555555555eeee"
    local helper_script="$TEST_TEMP_DIR/digest-rejected-override-second-call-helper.sh"
    cat > "$helper_script" << EOF
#!/bin/bash
set -uo pipefail

# If the rejection were ever "forgotten" on a second call, this mock would
# report a resolvable digest, letting the test catch the resurrection.
docker() {
    if [ "\$1" = "image" ] && [ "\$2" = "inspect" ]; then
        printf '%s\n' "$image_digest"
        return 0
    fi
    return 0
}

source "$DEPLOY_SCRIPT"

# Malformed: a full repo@sha256:<hex> reference is not the bare form the API
# requires.
ORCASLICER_CONTAINER_DIGEST="$malformed_override"
ENABLE_ORCA_WORKER=yes

resolve_orcaslicer_container_digest
echo "FIRST=[\$ORCASLICER_CONTAINER_DIGEST]"
echo "FIRST_CALIBRATION=\$ORCASLICER_CALIBRATION_AVAILABLE"

resolve_orcaslicer_container_digest
echo "SECOND=[\$ORCASLICER_CONTAINER_DIGEST]"
echo "SECOND_CALIBRATION=\$ORCASLICER_CALIBRATION_AVAILABLE"
EOF
    chmod +x "$helper_script"

    capture_output "$helper_script 2>&1 || true"
    local output
    output=$(get_output)

    assert_contains "$output" "FIRST=[]" "First call should reject the malformed override"
    assert_contains "$output" "FIRST_CALIBRATION=no" "Calibration must be unavailable after the first call rejects the override"
    assert_contains "$output" "SECOND=[]" "Second call must keep the rejection, not resurrect it via a fresh docker resolution"
    assert_contains "$output" "SECOND_CALIBRATION=no" "Calibration must stay unavailable on the second call too"

    rm -f "$helper_script" 2>/dev/null || true

    pass_test
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Deploy Docker Script Tests"
fi
