#!/bin/bash

# test-deploy-docker.sh - Tests for the main deployment script
# Tests argument parsing, configuration validation, and deployment logic

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
    
    # Create a mock .deploy-config to avoid interactive prompts
    cat > "$TEST_TEMP_DIR/.deploy-config" << 'EOF'
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
ORCASLICER_VERSION=2.3.1
EOF
}

teardown() {
    cd "$ORIGINAL_PWD" 2>/dev/null || true
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
    assert_contains "$output" "--architecture" "Help should mention architecture option"
    assert_contains "$output" "monolithic|microservices|host-network" "Help should list architecture options"
    assert_not_contains "$output" "multistage" "Help should not contain multistage as architecture option"
    
    pass_test
}

# Test architecture validation
test_architecture_validation() {
    start_test "architecture validation"
    
    # Valid architectures should not fail immediately
    capture_output "$DEPLOY_SCRIPT --architecture monolithic --dry-run --batch --output-dir $TEST_TEMP_DIR 2>&1 || true"
    local output=$(get_output)
    assert_not_contains "$output" "Invalid architecture" "Valid architecture should not show error"
    
    # Invalid architecture should fail
    assert_exit_code 1 "$DEPLOY_SCRIPT --architecture invalid --dry-run --batch --output-dir $TEST_TEMP_DIR 2>/dev/null"
    
    pass_test
}

# Test dry-run mode
test_dry_run_mode() {
    start_test "dry-run mode execution"
    
    # Deploy script must be run from PrintFarmer root directory
    local original_dir=$(pwd)
    cd "$REPO_ROOT"
    
    capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture monolithic 2>&1 || true"
    local output=$(get_output)
    
    # Return to original directory
    cd "$original_dir"
    
    assert_contains "$output" "Setup completed successfully" "Dry-run should complete successfully"
    assert_contains "$output" "To deploy:" "Dry-run should show deployment command"
    
    pass_test
}

# Test batch mode
test_batch_mode() {
    start_test "batch mode execution"
    
    # Deploy script must be run from PrintFarmer root directory
    local original_dir=$(pwd)
    cd "$REPO_ROOT"
    
    capture_output "timeout 30 $DEPLOY_SCRIPT --batch --dry-run --architecture monolithic 2>&1 || true"
    local output=$(get_output)
    
    # Return to original directory
    cd "$original_dir"
    
    # Should not prompt for input in batch mode and should complete successfully
    assert_contains "$output" "Setup completed successfully" "Batch mode should complete successfully"
    assert_contains "$output" "Dry-run" "Should indicate dry-run mode"
    
    pass_test
}

# Test configuration file generation
test_config_file_generation() {
    start_test "configuration file generation"
    
    # Deploy script must be run from PrintFarmer root directory
    local original_dir=$(pwd)
    cd "$REPO_ROOT"
    
    capture_output "timeout 30 $DEPLOY_SCRIPT --dry-run --batch --architecture monolithic 2>&1 || true"
    local output=$(get_output)
    
    # Return to original directory
    cd "$original_dir"
    
    # Check that .env file is mentioned or created
    assert_contains "$output" ".env" "Should mention environment file creation"
    
    pass_test
}

# Test environment variable settings
test_environment_variables() {
    start_test "environment variable configuration"
    
    # Test with command line architecture specification using helper function
    capture_output "$(get_deploy_script_command --architecture microservices --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should use specified architecture"
    
    pass_test
}

# Test no Redis configuration
test_no_redis_configuration() {
    start_test "no Redis configuration prompts"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "timeout 30 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices 2>&1 || true"
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
    
    capture_output "timeout 30 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices 2>&1 || true"
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
    
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture monolithic)"
    local output=$(get_output)
    
    # Should complete without port conflicts (assuming ports are free)
    assert_contains "$output" "Setup completed successfully" "Should complete with custom ports"
    
    unset API_PORT WEB_PORT
    
    pass_test
}

# Test architecture-specific configuration
test_architecture_specific_config() {
    start_test "architecture-specific configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Test monolithic architecture using helper function
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture monolithic)"
    local monolithic_output=$(get_output)
    
    assert_contains "$monolithic_output" "Monolithic" "Should indicate monolithic deployment"
    
    # Test microservices architecture using helper function
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture microservices)"
    local microservices_output=$(get_output)
    
    assert_contains "$microservices_output" "microservices" "Should indicate microservices deployment"
    
    pass_test
}

# Test worker configuration
test_worker_configuration() {
    start_test "worker configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Create configuration file for deploy script
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
DB_PROVIDER=postgres
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Clean up config file
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "Orca Workers: 2" "Should show configured Orca worker count"
    
    pass_test
}

# Test network configuration
test_network_configuration() {
    start_test "network configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Create configuration file for deploy script
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
NETWORK_MODE=bridge
DISCOVERY_RANGES=192.168.1.0/24,10.0.0.0/8
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Clean up config file
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "Configured ranges:" "Should show configured discovery ranges"
    
    pass_test
}

# Test database provider configuration
test_database_configuration() {
    start_test "database provider configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Test PostgreSQL
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=postgres
EOF
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local postgres_output=$(get_output)
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$postgres_output" "postgres" "Should configure PostgreSQL"
    
    # Test SQL Server
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=sqlserver
EOF
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local sqlserver_output=$(get_output)
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$sqlserver_output" "sqlserver" "Should configure SQL Server"
    
    pass_test
}

# Test all database providers with all architectures
test_all_database_architecture_combinations() {
    start_test "all database and architecture combinations"
    
    cd "$TEST_TEMP_DIR"
    
    local architectures=("monolithic" "microservices" "host-network")
    local databases=("postgres" "sqlserver" "mysql")
    
    for arch in "${architectures[@]}"; do
        for db in "${databases[@]}"; do
            # Create config file for this combination
            cat > "$REPO_ROOT/.deploy-config" << EOF
ARCHITECTURE=$arch
DB_PROVIDER=$db
EOF
            
            capture_output "$(get_deploy_script_command --dry-run --batch)"
            local output=$(get_output)
            
            # Clean up config file
            rm -f "$REPO_ROOT/.deploy-config"
            
            assert_contains "$output" "$db" "Should configure $db for $arch architecture"
            assert_contains "$output" "$arch" "Should show $arch architecture with $db database"
        done
    done
    
    pass_test
}

# Test addon configurations
test_addon_configurations() {
    start_test "addon stack configurations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test using command line arguments instead of environment variables
    capture_output "$(get_deploy_script_command --architecture microservices --include-monitoring --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should complete with addon enabled"
    
    pass_test
}

# Test comprehensive deployment combinations
test_comprehensive_deployment_combinations() {
    start_test "comprehensive deployment combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test maximum configuration using config file
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_SPOOLMAN=yes
EOF
    
    capture_output "$(get_deploy_script_command --architecture microservices --include-monitoring --dry-run --batch)"
    local output=$(get_output)
    
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "microservices" "Should configure microservices architecture"
    assert_contains "$output" "postgres" "Should configure PostgreSQL database"
    assert_contains "$output" "Setup completed successfully" "Should complete full configuration"
    
    # Test minimal configuration using config file
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=monolithic
DB_PROVIDER=sqlite
ENABLE_ORCA_WORKER=no
ENABLE_SPOOLMAN=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "monolithic" "Should configure monolithic architecture"
    assert_contains "$output" "Setup completed successfully" "Should complete minimal configuration"
    
    unset ARCHITECTURE DB_PROVIDER ENABLE_DISTRIBUTED_SLICING ENABLE_ORCA_WORKER ENABLE_SPOOLMAN
    
    pass_test
}

# Test configuration persistence
test_configuration_persistence() {
    start_test "configuration persistence"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture microservices)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should save configuration"
    
    pass_test
}

# Test validation logic
test_validation_logic() {
    start_test "configuration validation logic"
    
    cd "$TEST_TEMP_DIR"
    
    # Test basic validation by running deploy script
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture microservices)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should perform validation and complete"
    
    pass_test
}

# Test multistage build integration
test_multistage_build_integration() {
    start_test "multistage build integration"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "$(get_deploy_script_command --dry-run --batch --architecture monolithic)"
    local output=$(get_output)
    
    # Should complete successfully (multistage builds are internal implementation)
    assert_contains "$output" "Setup completed successfully" "Should complete with multistage builds"
    
    pass_test
}

# Run all tests
run_all_tests() {
    setup
    
    test_help_output
    test_architecture_validation
    test_dry_run_mode
    test_batch_mode
    test_config_file_generation
    test_environment_variables
    test_no_redis_configuration
    test_no_prusaslicer_configuration
    test_port_validation
    test_architecture_specific_config
    test_worker_configuration
    test_network_configuration  
    test_database_configuration
    test_all_database_architecture_combinations
    test_addon_configurations
    test_comprehensive_deployment_combinations
    test_configuration_persistence
    test_validation_logic
    test_multistage_build_integration
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Deploy Docker Script Tests"
fi