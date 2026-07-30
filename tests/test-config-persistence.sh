#!/bin/bash

# test-config-persistence.sh - Test configuration persistence for monitoring/telemetry/security
# Tests that interactive choices are properly saved and loaded

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"

# Source test framework
source "$SCRIPT_DIR/test-framework.sh"

# Test configuration
TEST_TEMP_DIR=""
ORIGINAL_PWD=""
REPO_BACKUP_DIR=""
TEARDOWN_COMPLETE=false

setup() {
    setup_test_environment
    TEST_TEMP_DIR=$(create_test_temp_dir)
    ORIGINAL_PWD=$(pwd)
    REPO_BACKUP_DIR="$TEST_TEMP_DIR/repository-artifacts"
    test_info "Using temp directory: $TEST_TEMP_DIR"
    backup_repository_deployment_artifacts "$REPO_ROOT" "$REPO_BACKUP_DIR"
    trap teardown EXIT
}

teardown() {
    if [[ "$TEARDOWN_COMPLETE" == "true" ]]; then
        return
    fi

    cd "$ORIGINAL_PWD" 2>/dev/null || true
    restore_repository_deployment_artifacts "$REPO_ROOT" "$REPO_BACKUP_DIR"
    TEARDOWN_COMPLETE=true
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
    teardown_test_environment
}

write_base_config() {
    local config_file="$1"
    cat > "$config_file" << 'EOF'
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
INCLUDE_DISCOVERY=false
ENABLE_DISTRIBUTED_SLICING=false
ENABLE_ORCA_WORKER=no
ORCA_WORKER_COUNT=0
ENABLE_SPOOLMAN=no
USE_EXTERNAL_STORAGE=no
EOF
}

# Test that monitoring/telemetry/security settings are saved to config file
test_monitoring_config_persistence() {
    start_test "monitoring/telemetry/security configuration persistence"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=true
INCLUDE_REGISTRY=false
EOF
    
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --batch 2>&1 || true"
    local output
    output=$(get_output)
    
    if [[ -f ".deploy-config" ]]; then
        local new_config_content
        new_config_content=$(cat ".deploy-config")
        
        # Check that monitoring/telemetry/security settings are saved
        assert_contains "$new_config_content" "INCLUDE_MONITORING=" "Config should contain INCLUDE_MONITORING setting"
        assert_contains "$new_config_content" "INCLUDE_TELEMETRY=" "Config should contain INCLUDE_TELEMETRY setting"
        assert_contains "$new_config_content" "INCLUDE_SECURITY=" "Config should contain INCLUDE_SECURITY setting"
        assert_contains "$new_config_content" "INCLUDE_REGISTRY=" "Config should contain INCLUDE_REGISTRY setting"
        
        # Check that the values are correct
        assert_contains "$new_config_content" "INCLUDE_MONITORING=true" "Config should save monitoring=true"
        assert_contains "$new_config_content" "INCLUDE_TELEMETRY=false" "Config should save telemetry=false"
        assert_contains "$new_config_content" "INCLUDE_SECURITY=true" "Config should save security=true"
        assert_contains "$new_config_content" "INCLUDE_REGISTRY=false" "Config should save registry=false"
        
    else
        fail_test "Config file was not created"
        return 1
    fi
    
    pass_test
}

# Test that CLI flags override config file settings
test_cli_flag_override() {
    start_test "CLI flags override config file settings"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
EOF
    
    # Run deploy script with CLI flags to enable monitoring
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --include-monitoring --include-security --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # The CLI override must be visible and persisted for future non-interactive runs.
    if [[ "$output" != *"enabled via CLI flag"* ]]; then
        test_info "CLI override output: $output"
    fi

    assert_contains "$output" "enabled via CLI flag" "Should indicate CLI flag override"
    local persisted_config
    persisted_config=$(cat ".deploy-config")
    assert_contains "$persisted_config" "INCLUDE_MONITORING=true" "Should persist the monitoring CLI override"
    assert_contains "$persisted_config" "INCLUDE_SECURITY=true" "Should persist the security CLI override"
    
    pass_test
}

# Test configuration loading displays monitoring settings
test_config_loading_display() {
    start_test "configuration loading displays monitoring settings"
    
    cd "$TEST_TEMP_DIR"
    
    write_base_config ".deploy-config"
    cat >> ".deploy-config" << 'EOF'
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=true
EOF
    
    # Run deploy script to see if it loads and displays the config properly
    capture_output "timeout 60 '$DEPLOY_SCRIPT' --config-file .deploy-config --env-file .env --output-dir generated --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # Should show that previous configuration was loaded
    assert_contains "$output" "Loaded configuration" "Should indicate config was loaded"
    
    pass_test
}

# Run all tests
run_all_tests() {
    setup
    
    test_monitoring_config_persistence
    test_cli_flag_override
    test_config_loading_display
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Configuration Persistence Tests"
fi