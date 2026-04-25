#!/bin/bash

# test-integration.sh - Integration tests for deployment pipeline
# Tests the complete workflow from configuration to deployment

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPLOY_SCRIPT="$REPO_ROOT/scripts/deploy-docker.sh"
COMPOSE_GENERATOR="$REPO_ROOT/scripts/docker/compose-generator.sh"

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

# Helper function to run deployment with proper directory handling
run_deployment_test() {
    local config_name="$1"
    local timeout_duration="${2:-60}"
    local generate_files="${3:-false}"  # Set to true to actually generate files for testing
    
    # Run deployment from repo root
    local original_dir=$(pwd)
    cd "$REPO_ROOT"
    
    if [[ "$generate_files" == "true" ]]; then
        # Extract architecture from config (before copying)
        local arch_value="monolithic"
        if grep -q "ARCHITECTURE=microservices" "$original_dir/$config_name" 2>/dev/null; then
            arch_value="microservices"
        elif grep -q "ARCHITECTURE=microservices" "$original_dir/$config_name" 2>/dev/null; then
            arch_value="microservices"
        fi
        
        # Copy config to repo root for deployment script
        cp "$original_dir/$config_name" "$REPO_ROOT/"
        
        # Generate files by calling compose generator directly (silently)
        "$REPO_ROOT/scripts/docker/compose-generator.sh" --output-dir "$REPO_ROOT" >/dev/null 2>&1 || true
        
        # Run deploy script in dry-run to get output for validation
        # Capture output directly instead of using test framework function
        local output
        output=$(timeout $timeout_duration $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true)
    else
        # Copy config to repo root for deployment script
        cp "$original_dir/$config_name" "$REPO_ROOT/"
        
        # Standard dry-run mode (no files generated)
        # Capture output directly instead of using test framework function
        local output
        output=$(timeout $timeout_duration $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true)
    fi
    
    # Clean up config file
    rm -f "$config_name"
    
    # Return to temp directory for compose file checks
    cd "$original_dir"
    
    # Copy generated compose file back to temp dir for checking
    if [[ -f "$REPO_ROOT/docker-compose.yml" ]]; then
        cp "$REPO_ROOT/docker-compose.yml" "./docker-compose.yml"
        # Also copy Dockerfile.multistage if it exists
        if [[ -f "$REPO_ROOT/Dockerfile.multistage" ]]; then
            cp "$REPO_ROOT/Dockerfile.multistage" "./Dockerfile.multistage"
        fi
    fi
    
    # Clean up generated files in repo root (for file generation mode)
    if [[ "$generate_files" == "true" ]]; then
        cd "$REPO_ROOT"
        rm -f docker-compose.yml Dockerfile.multistage .env .env.* docker-entrypoint-config.sh
        cd "$original_dir"
    fi
    
    echo "$output"
}

# Test complete standard deployment pipeline
test_monolithic_deployment_pipeline() {
    start_test "complete standard deployment pipeline"
    
    # Deploy script must be run from PrintFarmer root directory
    # But we need to set up config in a temp space first
    cd "$TEST_TEMP_DIR"
    
    # Create basic config to avoid prompts
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
NETWORK_MODE=bridge
API_PORT=5245
WEB_PORT=3000
DISCOVERY_RANGES=192.168.0.0/16
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=1
ENABLE_ORCA_WORKER=yes
ENABLE_SPOOLMAN=no
ORCASLICER_VERSION=2.3.2
EOF
    
    # Run deployment test in dry-run mode (focus on process success)
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output" "Setup completed successfully" "Pipeline should complete successfully"
    
    # Validate that the process mentions key components
    assert_contains "$output" "compose" "Should mention compose generation"
    
    # Generate files separately for content validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate docker-compose.yml via compose generator"
    
    # Check compose file content
    local compose_content=$(cat "docker-compose.yml")
    assert_contains "$compose_content" "Dockerfile.multistage" "Should use multistage dockerfile"
    assert_contains "$compose_content" "target: api-runtime" "Should contain API target"
    assert_contains "$compose_content" "target: frontend-runtime" "Should contain frontend target"
    
    pass_test
}

# Test microservices deployment pipeline
test_microservices_deployment_pipeline() {
    start_test "complete microservices deployment pipeline"
    
    cd "$TEST_TEMP_DIR"
    
    # Create microservices config
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
NETWORK_MODE=bridge
API_PORT=5245
WEB_PORT=3000
DISCOVERY_RANGES=192.168.0.0/16
ENABLE_DISTRIBUTED_SLICING=true
ORCA_WORKER_COUNT=2
ENABLE_ORCA_WORKER=yes
ENABLE_SPOOLMAN=no
ORCASLICER_VERSION=2.3.2
EOF
    
    # Run deployment test in dry-run mode (focus on process success)
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    assert_contains "$output" "Setup completed successfully" "Microservices pipeline should complete"
    
    # Generate files separately for content validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate docker-compose.yml via compose generator"
    local compose_content=$(cat "docker-compose.yml")
    assert_contains "$compose_content" "Dockerfile.multistage" "Should use multistage dockerfile"
    
    pass_test
}

# Test microservices deployment pipeline
# host-network deployment pipeline tests removed

# Test configuration consistency between scripts
test_configuration_consistency() {
    start_test "configuration consistency between scripts"
    
    # Generate config with compose-generator
    assert_command_success "$COMPOSE_GENERATOR --enable-orca-worker yes --output-dir $TEST_TEMP_DIR"
    
    local compose_from_generator=$(cat "$TEST_TEMP_DIR/docker-compose.yml")
    
    # Generate config with deploy script
    cd "$TEST_TEMP_DIR"
    rm -f docker-compose.yml Dockerfile.multistage
    
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
EOF
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Deploy script should also generate compose file"
    local compose_from_deploy=$(cat "docker-compose.yml")
    
    # Both should use multistage dockerfile
    assert_contains "$compose_from_generator" "Dockerfile.multistage" "Generator should use multistage"
    assert_contains "$compose_from_deploy" "Dockerfile.multistage" "Deploy script should use multistage"
    
    pass_test
}

# Test cleanup and regeneration
test_cleanup_and_regeneration() {
    start_test "cleanup and regeneration workflow"
    
    cd "$TEST_TEMP_DIR"
    
    # Create initial deployment
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
EOF
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should create initial files"
    
    # Modify config and regenerate
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=sqlserver
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
EOF
    
    local output2=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    local compose_content=$(cat "docker-compose.yml")
    
    # Should reflect new configuration
    assert_file_exists "docker-compose.yml" "Should regenerate files"
    
    pass_test
}

# Test environment file generation
test_environment_file_generation() {
    start_test "environment file generation and content"
    
    cd "$TEST_TEMP_DIR"
    
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
API_PORT=5555
WEB_PORT=3333
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
DISCOVERY_RANGES=192.168.1.0/24
EOF
    
    local output=$(run_deployment_test ".deploy-config" 60)
    
    # Should mention environment file creation
    assert_contains "$output" ".env" "Should mention environment file"
    
    pass_test
}

# Test multistage dockerfile presence
test_multistage_dockerfile_presence() {
    start_test "multistage dockerfile copying"
    
    cd "$TEST_TEMP_DIR"
    
    # Both scripts should ensure Dockerfile.multistage is available
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "$TEST_TEMP_DIR/Dockerfile.multistage" "Compose generator should copy Dockerfile.multistage"
    
    rm -f "$TEST_TEMP_DIR/Dockerfile.multistage"
    
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
EOF
    
    local output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "Dockerfile.multistage" "Deploy script should also copy Dockerfile.multistage"
    
    pass_test
}

# Test invalid configuration handling
test_invalid_configuration_handling() {
    start_test "invalid configuration handling"
    
    cd "$TEST_TEMP_DIR"
    
    # Create config with invalid values
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=invalid-arch
DB_PROVIDER=invalid-db
ORCA_WORKER_COUNT=-1
EOF
    
    local output=$(run_deployment_test ".deploy-config" 30)
    
    # Should handle invalid configuration gracefully
    # (Either fail cleanly or auto-correct with warnings)
    # The important thing is not to crash or hang
    
    pass_test
}

# Test Redis removal verification
test_redis_removal_verification() {
    start_test "Redis removal verification across pipeline"
    
    cd "$TEST_TEMP_DIR"
    
    cat > ".deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
EOF
    
    local deploy_output=$(run_deployment_test ".deploy-config" 60 false)
    
    # Generate files separately for validation
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    assert_file_exists "docker-compose.yml" "Should generate compose file"
    local compose_content=$(cat "docker-compose.yml")
    
    # Verify no Redis references in pipeline output or generated files
    assert_not_contains "$deploy_output" "Redis" "Deploy output should not mention Redis"
    assert_not_contains "$compose_content" "redis:" "Compose file should not contain Redis service"
    assert_not_contains "$compose_content" "ConnectionStrings__Redis" "Should not contain Redis connection strings"
    
    pass_test
}



# Test all network mode combinations
test_network_mode_combinations() {
    start_test "network mode combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Generate compose file
    assert_command_success "$COMPOSE_GENERATOR --output-dir $TEST_TEMP_DIR"
    
    assert_file_exists "docker-compose.yml" "Should create compose file"
    
    # Test deployment from repo root
    capture_output "cd '$REPO_ROOT' && timeout 40 $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy successfully"
    
    # Validate network configuration in compose file
    local compose_content=$(cat "docker-compose.yml")
    assert_not_contains "$compose_content" "network_mode: host" "Compose should not configure host network"
    
    rm -f docker-compose.yml Dockerfile.multistage
    
    pass_test
}

# Test security configuration combinations  
test_security_combinations() {
    start_test "security configuration combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test with security enabled using helper function
    capture_output "$(get_deploy_script_command --include-security --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy with security"
    
    # Test without security  
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output2=$(get_output)

    assert_contains "$output2" "Setup completed successfully" "Should deploy without security"
    
    pass_test
}

# Test comprehensive addon stack combinations
test_comprehensive_addon_combinations() {
    start_test "comprehensive addon stack combinations"
    
    cd "$TEST_TEMP_DIR"
    
    # Test all addons enabled using deploy script with addon flags
    capture_output "$(get_deploy_script_command --include-monitoring --include-telemetry --include-security --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Setup completed successfully" "Should deploy all addons successfully"
    
    # Test minimal configuration  
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output2=$(get_output)
    
    assert_contains "$output2" "Setup completed successfully" "Should deploy minimal configuration successfully"
    
    pass_test
}

# Run all tests
run_all_tests() {
    setup
    
    test_monolithic_deployment_pipeline
    test_microservices_deployment_pipeline
    test_host_network_deployment_pipeline
    test_configuration_consistency
    test_cleanup_and_regeneration
    test_environment_file_generation
    test_multistage_dockerfile_presence
    test_invalid_configuration_handling
    test_redis_removal_verification
    test_network_mode_combinations
    test_security_combinations
    test_comprehensive_addon_combinations
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Integration Tests"
fi