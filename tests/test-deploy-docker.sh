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

# Test that generated DB password is included and propagated into ConnectionStrings__Default
test_generated_db_password_propagation() {
    start_test "generated DB password propagation"

    cd "$TEST_TEMP_DIR"

    # Run deploy script in dry-run batch mode to generate env file
    capture_output "$(get_deploy_script_command --architecture microservices --dry-run --batch)"
    local output=$(get_output)

    # Determine expected env file
    local env_file="$TEST_TEMP_DIR/.env.microservices"

    # The deploy script copies .env to repo root; it also writes the output in working dir
    if [ -f "$REPO_ROOT/.env.microservices" ]; then
        env_file="$REPO_ROOT/.env.microservices"
    fi

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
    capture_output "$(get_deploy_script_command --architecture microservices --dry-run --batch --env DB_PROVIDER=sqlserver)"
    local output=$(get_output)

    local env_file="$TEST_TEMP_DIR/.env.microservices"
    if [ -f "$REPO_ROOT/.env.microservices" ]; then
        env_file="$REPO_ROOT/.env.microservices"
    fi

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

# Test MySQL password propagation
test_generated_mysql_password_propagation() {
    start_test "generated MySQL password propagation"

    cd "$TEST_TEMP_DIR"

    # Run deploy script in dry-run batch mode selecting mysql
    capture_output "$(get_deploy_script_command --architecture microservices --dry-run --batch --env DB_PROVIDER=mysql)"
    local output=$(get_output)

    local env_file="$TEST_TEMP_DIR/.env.microservices"
    if [ -f "$REPO_ROOT/.env.microservices" ]; then
        env_file="$REPO_ROOT/.env.microservices"
    fi

    assert_file_exists "$env_file" "Expected generated env file $env_file"

    local my_pw
    my_pw=$(grep -E '^MYSQL_ROOT_PASSWORD=' "$env_file" | tail -1 | cut -d= -f2- || true)
    local conn
    conn=$(grep -E '^ConnectionStrings__Default=' "$env_file" | tail -1 | cut -d= -f2- || true)

    assert_not_equals "" "$my_pw" "MYSQL_ROOT_PASSWORD should be generated and present"
    assert_not_equals "" "$conn" "ConnectionStrings__Default should be present"

    if [[ "$conn" != *"$my_pw"* ]]; then
        fail_test "Connection string does not contain generated MYSQL_ROOT_PASSWORD"
    else
        pass_test
    fi
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
    capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    # The script should mention environment file creation
    assert_contains "$output" ".env" "Should mention .env creation"

    # Expect the generated env file for microservices
    assert_file_exists ".env.microservices" "Should have created .env.microservices"

    # Inspect contents
    local env_content
    env_content=$(cat .env.microservices)

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
    rm -f .deploy-config .env.microservices .env || true

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

    capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    assert_file_exists ".env.microservices" "Should have created .env.microservices for postgres"
    local env_content
    env_content=$(cat .env.microservices)

    assert_contains "$env_content" "POSTGRES_PASSWORD" "Env file should include POSTGRES_PASSWORD"
    assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env file should not include MSSQL_SA_PASSWORD when postgres selected"
    assert_not_contains "$env_content" "MYSQL_PASSWORD" "Env file should not include MYSQL_PASSWORD when postgres selected"
    assert_contains "$env_content" "ConnectionStrings__Default" "Env file should include ConnectionStrings__Default"

    # Masked summary in output
    assert_contains "$output" "PostgreSQL credentials included (masked):" "Deploy output should include masked Postgres credentials header"
    assert_contains "$output" "POSTGRES_PASSWORD" "Deploy output should print masked POSTGRES_PASSWORD"

    rm -f .deploy-config .env.microservices .env || true

    pass_test
}

# End-to-end: mysql provider-only env generation and masked summary
test_env_provider_only_end_to_end_mysql() {
    start_test "deploy script generates provider-only .env for mysql"

    cd "$TEST_TEMP_DIR"

    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=mysql
EOF

    capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    assert_file_exists ".env.microservices" "Should have created .env.microservices for mysql"
    local env_content
    env_content=$(cat .env.microservices)

    assert_contains "$env_content" "MYSQL_PASSWORD" "Env file should include MYSQL_PASSWORD"
    assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env file should not include MSSQL_SA_PASSWORD when mysql selected"
    assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env file should not include POSTGRES_PASSWORD when mysql selected"
    assert_contains "$env_content" "ConnectionStrings__Default" "Env file should include ConnectionStrings__Default"

    # Masked summary in output
    assert_contains "$output" "MySQL credentials included (masked):" "Deploy output should include masked MySQL credentials header"
    assert_contains "$output" "MYSQL_PASSWORD" "Deploy output should print masked MYSQL_PASSWORD"

    rm -f .deploy-config .env.microservices .env || true

    pass_test
}

# End-to-end: monolithic provider-only env generation for providers
test_env_provider_monolithic_providers() {
    start_test "deploy script (monolithic) provider-only env generation"

    cd "$TEST_TEMP_DIR"

    local providers=("postgres" "sqlserver" "mysql")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=monolithic
DB_PROVIDER=$provider
EOF

        capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture monolithic --config-file .deploy-config 2>&1 || true"
        local output=$(get_output)

        # Expect .env.monolithic
        assert_file_exists ".env.monolithic" "Should have created .env.monolithic for $provider"
        local env_content
        env_content=$(cat .env.monolithic)

        case "$provider" in
            postgres)
                assert_contains "$env_content" "POSTGRES_PASSWORD" "Env should include POSTGRES_PASSWORD for monolithic+postgres"
                assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should not include MSSQL_SA_PASSWORD for monolithic+postgres"
                assert_contains "$output" "PostgreSQL credentials included (masked):" "Output should include masked Postgres header"
                ;;
            sqlserver)
                assert_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should include MSSQL_SA_PASSWORD for monolithic+sqlserver"
                assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env should not include POSTGRES_PASSWORD for monolithic+sqlserver"
                assert_contains "$output" "SQL Server credentials included (masked):" "Output should include masked SQL Server header"
                ;;
            mysql)
                assert_contains "$env_content" "MYSQL_PASSWORD" "Env should include MYSQL_PASSWORD for monolithic+mysql"
                assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env should not include POSTGRES_PASSWORD for monolithic+mysql"
                assert_contains "$output" "MySQL credentials included (masked):" "Output should include masked MySQL header"
                ;;
        esac

        rm -f .deploy-config .env.monolithic .env || true
    done

    pass_test
}

# End-to-end: host-network provider-only env generation for providers
test_env_provider_hostnetwork_providers() {
    start_test "deploy script (host-network) provider-only env generation"

    cd "$TEST_TEMP_DIR"

    local providers=("postgres" "sqlserver" "mysql")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=host-network
DB_PROVIDER=$provider
EOF

        capture_output "timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture host-network --config-file ./.deploy-config 2>&1 || true"
        local output=$(get_output)

        # Host-network uses .env.microservices
        assert_file_exists ".env.microservices" "Should have created .env.microservices for host-network + $provider"
        local env_content
        env_content=$(cat .env.microservices)

        case "$provider" in
            postgres)
                assert_contains "$env_content" "POSTGRES_PASSWORD" "Env should include POSTGRES_PASSWORD for host-network+postgres"
                assert_contains "$output" "PostgreSQL credentials included (masked):" "Output should include masked Postgres header"
                ;;
            sqlserver)
                assert_contains "$env_content" "MSSQL_SA_PASSWORD" "Env should include MSSQL_SA_PASSWORD for host-network+sqlserver"
                assert_contains "$output" "SQL Server credentials included (masked):" "Output should include masked SQL Server header"
                ;;
            mysql)
                assert_contains "$env_content" "MYSQL_PASSWORD" "Env should include MYSQL_PASSWORD for host-network+mysql"
                assert_contains "$output" "MySQL credentials included (masked):" "Output should include masked MySQL header"
                ;;
        esac

        rm -f .deploy-config .env.microservices .env || true
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
    output=$(timeout 60 $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file ./.deploy-config 2>&1 || true)
    
    # Check that env file was created
    assert_file_exists ".env.microservices" "Should create .env.microservices"
    
    # Get the actual password from env file
    local env_content
    env_content=$(cat .env.microservices)
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

# Run all tests
run_all_tests() {
    setup
    
    test_help_output
    test_architecture_validation
    test_dry_run_mode
    test_batch_mode
    test_config_file_generation
    test_environment_variables
    test_password_not_logged_to_stdout
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
    test_env_provider_only_end_to_end
    test_env_provider_only_end_to_end_postgres
    test_env_provider_only_end_to_end_mysql
    test_env_provider_monolithic_providers
    test_env_provider_hostnetwork_providers
    
    teardown
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Deploy Docker Script Tests"
fi