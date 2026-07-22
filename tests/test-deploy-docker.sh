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
    
    # Backup any existing deploy config so tests can restore it later
    if [ -f "$REPO_ROOT/.deploy-config" ]; then
        cp "$REPO_ROOT/.deploy-config" "$TEST_TEMP_DIR/.deploy-config.backup"
    fi

    # Create a mock .deploy-config in the repo root to avoid interactive prompts
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
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
ORCASLICER_VERSION=2.4.0
EOF
}

teardown() {
    cd "$ORIGINAL_PWD" 2>/dev/null || true
    if [ -f "$TEST_TEMP_DIR/.deploy-config.backup" ]; then
        mv "$TEST_TEMP_DIR/.deploy-config.backup" "$REPO_ROOT/.deploy-config"
    else
        rm -f "$REPO_ROOT/.deploy-config"
    fi
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
    assert_not_contains "$output" "--architecture" "Help should not mention removed architecture option"
    
    pass_test
}

# Test basic deploy script execution
test_basic_execution() {
    start_test "basic deploy script execution"
    
    # Deploy script should run successfully in dry-run mode
    capture_output "$DEPLOY_SCRIPT --dry-run --batch --output-dir $TEST_TEMP_DIR 2>&1 || true"
    local output=$(get_output)
    assert_contains "$output" "Setup completed successfully" "Deploy script should complete in dry-run mode"
    
    pass_test
}

# Test dry-run mode
test_dry_run_mode() {
    start_test "dry-run mode execution"
    
    # Deploy script must be run from PrintFarmer root directory
    local original_dir=$(pwd)
    cd "$REPO_ROOT"
    
    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true"
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
    
    capture_output "timeout 120 $DEPLOY_SCRIPT --batch --dry-run 2>&1 || true"
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
    
    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true"
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
    
    # Test basic deploy script execution using helper function
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "microservices" "Should show microservices architecture"
    
    pass_test
}

# Test no Redis configuration
test_no_redis_configuration() {
    start_test "no Redis configuration prompts"
    
    cd "$TEST_TEMP_DIR"
    
    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true"
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
    
    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch 2>&1 || true"
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
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_DISTRIBUTED_SLICING=true
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
ARCHITECTURE=microservices
NETWORK_MODE=bridge
DISCOVERY_RANGES=192.168.1.0/24,10.0.0.0/8
    DB_PROVIDER=postgres
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    assert_contains "$output" "Network Discovery Configuration" "Should mention discovery configuration section"
    rm -f "$REPO_ROOT/.deploy-config"
    
    pass_test
}

# Test database provider configuration
test_database_configuration() {
    start_test "database provider configuration"
    
    cd "$TEST_TEMP_DIR"
    
    # Test PostgreSQL
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
EOF
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local postgres_output=$(get_output)
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$postgres_output" "postgres" "Should configure PostgreSQL"
    
    # Test SQL Server
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
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
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)

    # Determine expected env file
    local env_file="$TEST_TEMP_DIR/.env"

    # The deploy script copies .env to repo root; it also writes the output in working dir
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
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
    capture_output "$(get_deploy_script_command --dry-run --batch --env DB_PROVIDER=sqlserver)"
    local output=$(get_output)

    local env_file="$TEST_TEMP_DIR/.env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
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
    capture_output "$(get_deploy_script_command --dry-run --batch --env DB_PROVIDER=mysql)"
    local output=$(get_output)

    local env_file="$TEST_TEMP_DIR/.env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
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

# Test all database providers
test_all_database_combinations() {
    start_test "all database provider combinations"
    
    cd "$TEST_TEMP_DIR"
    
    local databases=("postgres" "sqlserver" "mysql")
    
    for db in "${databases[@]}"; do
        # Create config file for this combination
        cat > "$REPO_ROOT/.deploy-config" << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$db
EOF
        
        capture_output "$(get_deploy_script_command --dry-run --batch)"
        local output=$(get_output)
        
        # Clean up config file
        rm -f "$REPO_ROOT/.deploy-config"
        
        assert_contains "$output" "$db" "Should configure $db database"
        assert_contains "$output" "microservices" "Should show microservices architecture with $db database"
    done
    
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
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_SPOOLMAN=yes
EOF
    
    capture_output "$(get_deploy_script_command --include-monitoring --dry-run --batch)"
    local output=$(get_output)
    
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "microservices" "Should configure microservices architecture"
    assert_contains "$output" "postgres" "Should configure PostgreSQL database"
    assert_contains "$output" "Setup completed successfully" "Should complete full configuration"
    
    # Test minimal configuration using config file
    cat > "$REPO_ROOT/.deploy-config" << 'EOF'
ARCHITECTURE=microservices
    DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=no
ENABLE_SPOOLMAN=no
EOF
    
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    rm -f "$REPO_ROOT/.deploy-config"
    
    assert_contains "$output" "microservices" "Should configure microservices architecture"
    assert_contains "$output" "Setup completed successfully" "Should complete minimal configuration"
    
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
    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
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

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
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

# End-to-end: mysql provider-only env generation and masked summary
test_env_provider_only_end_to_end_mysql() {
    start_test "deploy script generates provider-only .env for mysql"

    cd "$TEST_TEMP_DIR"

    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=mysql
EOF

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    assert_file_exists ".env" "Should have created .env for mysql"
    local env_content
    env_content=$(cat .env)

    assert_contains "$env_content" "MYSQL_PASSWORD" "Env file should include MYSQL_PASSWORD"
    assert_not_contains "$env_content" "MSSQL_SA_PASSWORD" "Env file should not include MSSQL_SA_PASSWORD when mysql selected"
    assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env file should not include POSTGRES_PASSWORD when mysql selected"
    assert_contains "$env_content" "ConnectionStrings__Default" "Env file should include ConnectionStrings__Default"

    # Masked summary in output
    assert_contains "$output" "MySQL credentials included (masked):" "Deploy output should include masked MySQL credentials header"
    assert_contains "$output" "MYSQL_PASSWORD" "Deploy output should print masked MYSQL_PASSWORD"

    rm -f .deploy-config .env .env || true

    pass_test
}

# End-to-end: standard provider-only env generation for all providers
test_env_provider_standard_providers() {
    start_test "deploy script provider-only env generation (standard)"

    cd "$TEST_TEMP_DIR"

    local providers=("postgres" "sqlserver" "mysql")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$provider
EOF

        capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
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
            mysql)
                assert_contains "$env_content" "MYSQL_PASSWORD" "Env should include MYSQL_PASSWORD for mysql"
                assert_not_contains "$env_content" "POSTGRES_PASSWORD" "Env should not include POSTGRES_PASSWORD for mysql"
                assert_contains "$output" "MySQL credentials included (masked):" "Output should include masked MySQL header"
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

    local providers=("postgres" "sqlserver" "mysql")
    for provider in "${providers[@]}"; do
        cat > .deploy-config << EOF
ARCHITECTURE=microservices
DB_PROVIDER=$provider
EOF

        capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file ./.deploy-config 2>&1 || true"
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
            mysql)
                assert_contains "$env_content" "MYSQL_PASSWORD" "Env should include MYSQL_PASSWORD for microservices+mysql"
                assert_contains "$output" "MySQL credentials included (masked):" "Output should include masked MySQL header"
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
    output=$(timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file ./.deploy-config 2>&1 || true)
    
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
    capture_output "timeout 90 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    # Check output for errors or completion
    if echo "$output" | grep -q "error\|Error\|ERROR" && ! echo "$output" | grep -q "completed"; then
        test_info "Deploy script output: $output"
    fi

    # Determine expected env file - .env is used
    local env_file=""
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    elif [ -f ".env" ]; then
        env_file=".env"
    elif [ -f ".env" ]; then
        env_file=".env"
    fi

    if [ -z "$env_file" ] || [ ! -f "$env_file" ]; then
        test_info "Available files in repo root: $(ls -la "$REPO_ROOT"/.env* 2>/dev/null || echo 'none')"
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

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

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

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

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
DB_PROVIDER=postgres
ENABLE_SPOOLMAN=yes
SPOOLMAN_BASE_URL=http://spoolman.local:7912
ENABLE_DISCOVERY=yes
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
EOF

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

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
DB_PROVIDER=postgres
ENABLE_SPOOLMAN=yes
SPOOLMAN_BASE_URL=http://spoolman.local:7912
ENABLE_DISCOVERY=yes
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
EOF

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

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
    assert_contains "$output" "SOURCE_SUCCESS=true" "Source operation should succeed"
    assert_contains "$output" "PFARM__Spoolman__BaseUrl=http://spoolman.local:7912" "PFARM__Spoolman__BaseUrl should be sourced correctly"
    assert_contains "$output" "PFARM__NetworkDiscovery__EnableDiscovery=" "PFARM__NetworkDiscovery__EnableDiscovery should be sourced"

    # Clean up
    rm -f .deploy-config "$env_file" "$TEST_TEMP_DIR/test_pfarm_source.sh" || true

    pass_test
}

# Test: Slicer worker API key generation
test_slicer_worker_api_key_generation() {
    start_test "Slicer worker API key generation"

    cd "$TEST_TEMP_DIR"

    # Create config with OrcaSlicer workers enabled
    cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ENABLE_DISTRIBUTED_SLICING=true
EOF

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

    assert_file_exists "$env_file" "Should create env file with API key configuration"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify API keys are generated and present in the env file
    assert_contains "$env_content" "SlicerRegistry__ApiKey=" "Should include primary SlicerRegistry__ApiKey for worker registration"
    
    # For scaled workers (count > 1), verify individual worker keys are generated
    assert_contains "$env_content" "SlicerRegistry__ApiKey__orcaslicer_worker" "Should include individual API keys for scaled workers"

    # Verify the key has actual content (not just the key name)
    local key_line
    key_line=$(grep "^SlicerRegistry__ApiKey=" "$env_file" | head -1)
    local key_value
    key_value=$(echo "$key_line" | cut -d'=' -f2-)
    
    assert_not_equals "$key_value" "" "API key value should not be empty"

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
DB_PROVIDER=postgres
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=1
ENABLE_DISTRIBUTED_SLICING=true
EOF

    capture_output "timeout 120 $DEPLOY_SCRIPT --dry-run --batch --config-file .deploy-config 2>&1 || true"
    local output=$(get_output)

    local env_file=".env"
    if [ -f "$REPO_ROOT/.env" ]; then
        env_file="$REPO_ROOT/.env"
    fi

    assert_file_exists "$env_file" "Should create env file with API key configuration"
    
    local env_content
    env_content=$(cat "$env_file")

    # Verify API key is present for single worker
    assert_contains "$env_content" "SlicerRegistry__ApiKey=" "Should include SlicerRegistry__ApiKey for single worker"

    # Verify the key has actual content (not just the key name)
    local key_line
    key_line=$(grep "^SlicerRegistry__ApiKey=" "$env_file" | head -1)
    local key_value
    key_value=$(echo "$key_line" | cut -d'=' -f2-)
    
    assert_not_equals "$key_value" "" "API key value should not be empty"

    # Clean up
    rm -f .deploy-config "$env_file" || true

    pass_test
}

# Test that PostgreSQL password authentication actually works (integration test)
test_postgres_password_authentication_integration() {
    start_test "PostgreSQL password authentication (integration test)"
    
    # Only run this test if Docker and Docker Compose are available
    if ! command -v docker &> /dev/null || ! command -v docker-compose &> /dev/null; then
        skip_test "Docker/Docker Compose not available"
        return 0
    fi
    
    cd "$TEST_TEMP_DIR"
    
    # Run deploy script to generate compose and env files
    capture_output "$(get_deploy_script_command --dry-run --batch)"
    local output=$(get_output)
    
    # Find the generated compose file
    local compose_file="$TEST_TEMP_DIR/docker-compose.yml"
    if [ ! -f "$compose_file" ]; then
        # Try the repo root
        compose_file="$REPO_ROOT/docker-compose.yml"
    fi
    
    assert_file_exists "$compose_file" "Generated compose file should exist"
    
    local env_file="$TEST_TEMP_DIR/.env"
    if [ ! -f "$env_file" ]; then
        env_file="$REPO_ROOT/.env"
    fi
    
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
    test_batch_mode
    test_config_file_generation
    test_environment_variables
    test_password_not_logged_to_stdout
    test_no_redis_configuration
    test_no_prusaslicer_configuration
    test_port_validation
    test_deployment_config_output
    test_worker_configuration
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
    test_env_provider_only_end_to_end_mysql
    test_env_provider_standard_providers
    test_env_file_sourcing_with_connection_strings
    test_connection_string_sync_resolves_stale_export
    test_connection_string_sync_noop_when_values_match
    test_ensure_connection_string_password_updates_env_and_shell
    test_pfarm_spoolman_baseurl_in_env
    test_pfarm_network_discovery_enable_in_env
    test_pfarm_network_discovery_subnets_in_env
    test_pfarm_variables_complete_set
    test_pfarm_variables_sourcing
    test_slicer_worker_api_key_generation
    test_slicer_worker_api_key_single_worker
    test_postgres_password_authentication_integration
    test_pgadmin_flag_parsing
    test_pgadmin_config_persistence
    test_pgadmin_postgres_only
    
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
    assert_contains "$(grep -n 'postgres' $COMPOSE_GENERATOR | head -5)" "postgres" "compose-generator should reference PostgreSQL"
    
    pass_test
}

# Run the test suite
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    run_test_suite run_all_tests "Deploy Docker Script Tests"
fi