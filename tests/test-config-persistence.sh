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

setup() {
    setup_test_environment
    TEST_TEMP_DIR=$(create_test_temp_dir)
    ORIGINAL_PWD=$(pwd)
    test_info "Using temp directory: $TEST_TEMP_DIR"
}

teardown() {
    cd "$ORIGINAL_PWD" 2>/dev/null || true
    cleanup_test_temp_dir "$TEST_TEMP_DIR"
    teardown_test_environment
}

# Test that monitoring/telemetry/security settings are saved to config file
test_monitoring_config_persistence() {
    start_test "monitoring/telemetry/security configuration persistence"
    
    cd "$TEST_TEMP_DIR"
    
    # Create a config with monitoring settings
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=postgres
NETWORK_MODE=bridge
API_PORT=5245
WEB_PORT=3000
DISCOVERY_RANGES=192.168.0.0/16
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=1
ENABLE_ORCA_WORKER=yes
ENABLE_SPOOLMAN=no
ORCASLICER_VERSION=2.4.0
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=true
INCLUDE_REGISTRY=false
EOF
    
    # Run deploy script from repo root with batch mode
    capture_output "cd '$REPO_ROOT' && cp '$TEST_TEMP_DIR/.deploy-config' '$REPO_ROOT/.deploy-config' && timeout 60 '$DEPLOY_SCRIPT' --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # Check if new config file was created in repo root
    if [[ -f "$REPO_ROOT/.deploy-config" ]]; then
        local new_config_content=$(cat "$REPO_ROOT/.deploy-config")
        
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
        
        # Clean up
        rm -f "$REPO_ROOT/.deploy-config"
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
    
    # Create a config with monitoring disabled
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=postgres
INCLUDE_MONITORING=false
INCLUDE_TELEMETRY=false
INCLUDE_SECURITY=false
INCLUDE_REGISTRY=false
EOF
    
    # Run deploy script with CLI flags to enable monitoring
    capture_output "cd '$REPO_ROOT' && timeout 60 '$DEPLOY_SCRIPT' --include-monitoring --include-security --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # Should mention that monitoring is enabled via CLI flag
    assert_contains "$output" "enabled via CLI flag" "Should indicate CLI flag override"
    
    # Clean up
    rm -f "$REPO_ROOT/.deploy-config"
    
    pass_test
}

# Test configuration loading displays monitoring settings
test_config_loading_display() {
    start_test "configuration loading displays monitoring settings"
    
    cd "$TEST_TEMP_DIR"
    
    # Create a config with some monitoring settings
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=postgres
INCLUDE_MONITORING=true
INCLUDE_TELEMETRY=true
EOF
    
    # Run deploy script to see if it loads and displays the config properly
    capture_output "cd '$REPO_ROOT' && timeout 30 '$DEPLOY_SCRIPT' --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    # Should show that previous configuration was loaded
    assert_contains "$output" "Loaded configuration" "Should indicate config was loaded"
    
    # Clean up
    rm -f "$REPO_ROOT/.deploy-config"
    
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