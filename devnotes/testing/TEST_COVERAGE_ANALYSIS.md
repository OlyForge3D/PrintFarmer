# Test Coverage Analysis: deploy-docker.sh & compose-generator.sh

**Last Updated**: November 1, 2025  
**Current Status**: ✅ 44/44 tests passing (20 compose-generator, 24 deploy-docker)

## Overview

This document identifies gaps in test coverage for the deployment scripts and provides recommendations for TDD-first development. The analysis ensures that new features are tested **before** implementation and that critical functionality is properly validated.

---

## 1. Compose-Generator Test Coverage

### Current Tests (20/20 passing ✅)

**Architecture & Configuration**:
- ✅ Help output
- ✅ Invalid architecture handling
- ✅ Monolithic architecture generation
- ✅ Microservices architecture generation

**OrcaSlicer Workers**:
- ✅ OrcaSlicer worker configuration
- ✅ OrcaSlicer worker variations (different counts)
- ✅ PrusaSlicer worker disabled

**Database Support**:
- ✅ Database provider configuration
- ✅ All database providers (postgres, sqlserver, mysql)
- ✅ Provider-only .env for sqlserver

**Addon Stacks**:
- ✅ Monitoring stack inclusion
- ✅ All addon stacks (monitoring, telemetry, security, registry)
- ✅ Combined addon stacks

**Quality & Integration**:
- ✅ Dry-run mode
- ✅ Output directory creation
- ✅ Multistage dockerfile targets
- ✅ No Redis references in output
- ✅ No PrusaSlicer references in output
- ✅ All architecture + database combinations
- ✅ All architecture + addon combinations

---

## 2. Missing Tests for Compose-Generator 🔴

### Critical Missing Tests (Should Implement First via TDD)

#### 2.1 Error Handling & Validation

**Test: `test_missing_required_architecture_argument`**
```bash
# Should fail when --architecture is not provided
$COMPOSE_GENERATOR --output-dir /tmp/test
# Expected: Exit code 1, error message about missing architecture
```
- **Rationale**: Silent failures or defaults could mask configuration errors
- **TDD Approach**: Write test first, verify it fails, then add validation to generator
- **Complexity**: LOW
- **Risk if Missing**: Users might get default config when they forgot to specify architecture

---

#### 2.2 File System Operations

**Test: `test_permission_denied_output_directory`**
```bash
# Should handle permissions gracefully
mkdir -p /tmp/readonly
chmod 000 /tmp/readonly
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/readonly
# Expected: Error message, exit code 1, no partial files
chmod 755 /tmp/readonly
```
- **Rationale**: Deployment failures due to permissions are frustrating to debug
- **TDD Approach**: Setup readonly dir test, verify generator reports error clearly
- **Complexity**: MEDIUM
- **Risk if Missing**: Permission errors could leave partial/corrupted compose files

---

**Test: `test_output_directory_does_not_exist`**
```bash
# Should create parent directories
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/nonexistent/deeply/nested/dir
# Expected: Exit code 0, directories created, files present
```
- **Rationale**: Users expect `--output-dir` to work like `mkdir -p`
- **TDD Approach**: Write test with non-existent path, implement directory creation
- **Complexity**: LOW
- **Risk if Missing**: Users forced to create directories manually

---

**Test: `test_overwrite_existing_compose_file`**
```bash
# Should handle existing compose file
echo "existing content" > /tmp/test/docker-compose.yml
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
# Expected: File overwritten with new content (no warning prompt in non-interactive mode)
```
- **Rationale**: Need clear behavior on overwriting - no interactive prompts in scripts
- **TDD Approach**: Write test expecting overwrite, verify file content changed
- **Complexity**: LOW
- **Risk if Missing**: Unclear overwrite behavior could corrupt deployments

---

#### 2.3 Database Configuration Edge Cases

**Test: `test_database_env_file_generation_format`**
```bash
# Verify .env file format for each provider
$COMPOSE_GENERATOR --architecture microservices --db-provider postgres --output-dir /tmp/test
# Expected: .env.microservices contains properly formatted KEY=VALUE pairs
# Should handle special characters, spaces, quotes properly
```
- **Rationale**: Malformed env files break deployments silently
- **TDD Approach**: Define expected format, test each provider compliance
- **Complexity**: MEDIUM
- **Risk if Missing**: Env file parsing errors hard to debug

---

**Test: `test_database_volume_naming_consistency`**
```bash
# Verify all databases use consistent volume naming: printfarmer-database
for provider in postgres sqlserver mysql; do
    $COMPOSE_GENERATOR --architecture microservices --db-provider $provider --output-dir /tmp/test-$provider
    # Expected: All use "printfarmer-database:" volume, not provider-specific names
done
```
- **Rationale**: Volume naming standardization is critical for backup/restore
- **TDD Approach**: Assert all providers use same volume name
- **Complexity**: LOW
- **Risk if Missing**: Volume naming inconsistency causes data loss on restore

---

**Test: `test_invalid_database_provider`**
```bash
# Should reject unknown database providers
$COMPOSE_GENERATOR --architecture microservices --db-provider nosuchdb --output-dir /tmp/test
# Expected: Exit code 1, clear error message
```
- **Rationale**: Typos in provider should fail fast, not silently default
- **TDD Approach**: Write test with invalid provider, add validation
- **Complexity**: LOW
- **Risk if Missing**: Silent defaults to wrong provider causes wrong schema

---

#### 2.4 Worker Configuration

**Test: `test_orcaslicer_worker_count_validation`**
```bash
# Should accept various formats and validate properly
for format in "yes" "no" "true" "false" "1" "2" "5" "0"; do
    $COMPOSE_GENERATOR --architecture microservices --enable-orca-worker "$format" --output-dir /tmp/test
    # Expected: Exit 0 for all, correct count in compose
done

# Invalid formats should fail
$COMPOSE_GENERATOR --architecture microservices --enable-orca-worker "invalid" --output-dir /tmp/test
# Expected: Exit code 1
```
- **Rationale**: Worker count is critical for performance, typos should fail fast
- **TDD Approach**: Define accepted formats, test all, verify rejection of invalid
- **Complexity**: MEDIUM
- **Risk if Missing**: Silent failures with wrong worker count affects 3D printer management

---

**Test: `test_worker_service_dependencies`**
```bash
# Verify worker services have correct depends_on configuration
$COMPOSE_GENERATOR --architecture microservices --enable-orca-worker yes --output-dir /tmp/test
local compose_content=$(cat /tmp/test/docker-compose.yml)
# Expected: orcaslicer-worker depends on api service (for health checks)
# Expected: Services are only included if worker count > 0
```
- **Rationale**: Dependency ordering affects startup reliability
- **TDD Approach**: Assert dependency chains are correct
- **Complexity**: MEDIUM
- **Risk if Missing**: Workers start before API, causing connection failures

---

#### 2.5 Addon Stack Integration

**Test: `test_addon_services_dont_conflict_with_core`**
```bash
# Verify addon services don't conflict with core services
$COMPOSE_GENERATOR --architecture microservices \
  --include-monitoring --include-telemetry --include-security --include-registry \
  --output-dir /tmp/test
local compose_content=$(cat /tmp/test/docker-compose.yml)
# Expected: No duplicate service names
# Expected: No duplicate volume names
# Expected: No duplicate network definitions
```
- **Rationale**: Service conflicts prevent deployment
- **TDD Approach**: Parse compose, assert no duplicates
- **Complexity**: MEDIUM
- **Risk if Missing**: Addon conflicts silently override services

---

**Test: `test_monitoring_stack_environment_variables`**
```bash
# Verify monitoring stack (Prometheus, Grafana, Loki) has required env vars
$COMPOSE_GENERATOR --architecture microservices --include-monitoring --output-dir /tmp/test
local compose=$(cat /tmp/test/docker-compose.yml)
# Expected: Grafana admin password (GRAFANA_ADMIN_PASSWORD)
# Expected: Prometheus configuration paths
# Expected: Loki storage configuration
```
- **Rationale**: Incomplete configs cause monitoring to silently fail
- **TDD Approach**: Assert required variables present
- **Complexity**: MEDIUM
- **Risk if Missing**: Monitoring silently doesn't work

---

**Test: `test_security_stack_tls_configuration`**
```bash
# Verify security stack includes TLS/certificate handling
$COMPOSE_GENERATOR --architecture microservices --include-security --output-dir /tmp/test
# Expected: Volume mounts for certificates
# Expected: TLS environment variables configured
# Expected: SSL/TLS ports configured
```
- **Rationale**: Incomplete TLS config compromises security
- **TDD Approach**: Assert TLS configuration present
- **Complexity**: HIGH
- **Risk if Missing**: Deployments not properly secured

---

**Test: `test_registry_stack_authentication`**
```bash
# Verify local registry stack has authentication configured
$COMPOSE_GENERATOR --architecture microservices --include-registry --output-dir /tmp/test
local compose=$(cat /tmp/test/docker-compose.yml)
# Expected: Registry authentication credentials
# Expected: htpasswd volume for user management
# Expected: Registry storage volume
```
- **Rationale**: Unauthenticated registries are security risk
- **TDD Approach**: Assert auth configuration
- **Complexity**: MEDIUM
- **Risk if Missing**: Insecure registry access

---

#### 2.6 YAML Validation & Compose Compliance

**Test: `test_generated_compose_file_is_valid_yaml`**
```bash
# Verify generated compose file is valid YAML
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
# Expected: `docker compose config --quiet /tmp/test/docker-compose.yml` succeeds
# This catches syntax errors, duplicate keys, etc.
```
- **Rationale**: Invalid YAML breaks deployments immediately
- **TDD Approach**: Run through docker compose validator
- **Complexity**: LOW
- **Risk if Missing**: Broken YAML caught too late (during deployment)

---

**Test: `test_no_unresolved_environment_variable_references`**
```bash
# Verify no ${UNDEFINED_VAR} references in output
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
local compose=$(cat /tmp/test/docker-compose.yml)
# Expected: No unreplaced ${VARIABLE} patterns (except ${VARIABLE} which should be preserved for runtime)
# Check that all deployment-time variables are resolved
```
- **Rationale**: Unresolved vars cause silent failures at runtime
- **TDD Approach**: Grep for unresolved patterns
- **Complexity**: MEDIUM
- **Risk if Missing**: Hard-to-debug runtime failures

---

**Test: `test_compose_file_service_names_valid`**
```bash
# Verify all service names follow Docker naming rules
# Service names: alphanumeric + hyphen/underscore, lowercase recommended
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
# Expected: All service names are lowercase, no spaces, no special chars
```
- **Rationale**: Invalid names cause subtle Docker errors
- **TDD Approach**: Assert name patterns
- **Complexity**: LOW
- **Risk if Missing**: Deployment fails with cryptic Docker errors

---

#### 2.7 Platform/Architecture Support

**Test: `test_elasticsearch_stack_availability_by_architecture`**
```bash
# Verify Elasticsearch stack support matches system architecture
# Currently: Disabled by default, only enabled on x86_64
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
# On ARM (Raspberry Pi): Expected to skip Elasticsearch, no error
# On x86_64 with ENABLE_ELASTIC_STACK=true: Expected to include
# On x86_64 with ENABLE_ELASTIC_STACK=false: Expected to skip
```
- **Rationale**: Architecture incompatibilities cause confusing errors
- **TDD Approach**: Test on different arch (or mock), verify correct behavior
- **Complexity**: HIGH
- **Risk if Missing**: Users on ARM get incompatible images

---

**Test: `test_dockerfile_architecture_targets_match_platform`**
```bash
# Verify dockerfile uses correct architecture-specific base images
# Multistage build should select correct runtime base
$COMPOSE_GENERATOR --architecture microservices --output-dir /tmp/test
# Expected: Dockerfile uses OS-neutral base images compatible with system
```
- **Rationale**: Wrong base images fail on different architectures
- **TDD Approach**: Assert correct base images in Dockerfile
- **Complexity**: HIGH
- **Risk if Missing**: Cross-platform deployments fail mysteriously

---

#### 2.8 Edge Cases & Stress Tests

**Test: `test_all_options_combined`**
```bash
# Stress test: All possible combinations work together
$COMPOSE_GENERATOR \
  --architecture microservices \
  --enable-orca-worker 2 \
  --db-provider mysql \
  --include-monitoring \
  --include-telemetry \
  --include-security \
  --include-registry \
  --output-dir /tmp/test
# Expected: All services present, no conflicts, valid YAML
```
- **Rationale**: Feature interactions can have unexpected side effects
- **TDD Approach**: Combinatorial test
- **Complexity**: MEDIUM
- **Risk if Missing**: Hidden bugs in feature interactions

---

**Test: `test_empty_or_null_optional_parameters`**
```bash
# Should handle optional parameters gracefully
$COMPOSE_GENERATOR --architecture microservices --enable-orca-worker "" --output-dir /tmp/test
# Expected: Either error or default behavior, not silent failure
```
- **Rationale**: Empty strings can cause unexpected behavior
- **TDD Approach**: Test with various empty/null inputs
- **Complexity**: LOW
- **Risk if Missing**: Silent defaults to wrong configuration

---

---

## 3. Deploy-Docker Test Coverage

### Current Tests (24/24 passing ✅)

**Core Functionality**:
- ✅ Help output
- ✅ Architecture validation
- ✅ Dry-run mode
- ✅ Batch mode
- ✅ Configuration file generation

**Configuration**:
- ✅ Environment variable configuration
- ✅ Architecture-specific configuration
- ✅ Database provider configuration
- ✅ Network configuration
- ✅ Worker configuration
- ✅ Addon stack configurations

**Credential Management**:
- ✅ Generated DB password propagation
- ✅ SQL Server password propagation
- ✅ MySQL password propagation

**Integration**:
- ✅ All database + architecture combinations
- ✅ Comprehensive deployment combinations
- ✅ Configuration persistence
- ✅ Multistage build integration
- ✅ No Redis configuration prompts
- ✅ No PrusaSlicer configuration prompts
- ✅ Port validation and conflict detection
- ✅ Configuration validation logic

---

## 4. Missing Tests for Deploy-Docker 🔴

### Critical Missing Tests (Should Implement First via TDD)

#### 4.1 Credential & Secret Management

**Test: `test_password_not_logged_to_stdout`**
```bash
# Verify passwords are masked in output
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices 2>&1 | tee /tmp/deploy.log
# Expected: No actual passwords in log (should show masked format)
grep -q "POSTGRES_PASSWORD=.*\*\*\*" /tmp/deploy.log
# Expected: Exit 0 (passwords are masked)
```
- **Rationale**: Passwords accidentally logged are security breach
- **TDD Approach**: Capture output, assert no plain passwords visible
- **Complexity**: HIGH
- **Risk if Missing**: Password exposure in logs, security audit failures

---

**Test: `test_password_complexity_requirements`**
```bash
# Verify generated passwords meet security requirements
# Generated passwords should be: >=16 chars, alphanumeric + special chars
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider postgres
grep "POSTGRES_PASSWORD=" .env.microservices | cut -d= -f2 | wc -c
# Expected: >= 16 characters
# Expected: Contains uppercase, lowercase, numbers, special chars
```
- **Rationale**: Weak passwords are security risk
- **TDD Approach**: Generate passwords, validate complexity
- **Complexity**: MEDIUM
- **Risk if Missing**: Weak default passwords compromise security

---

**Test: `test_user_provided_password_validation`**
```bash
# Verify user-provided passwords are validated
export DB_PASSWORD="weak"  # Only 4 chars
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: Either error or warning about weak password
```
- **Rationale**: Users should be warned about weak passwords
- **TDD Approach**: Provide weak password, verify validation
- **Complexity**: MEDIUM
- **Risk if Missing**: Users might use weak passwords

---

**Test: `test_connection_string_does_not_contain_credentials`**
```bash
# Verify ConnectionStrings__Default doesn't leak credentials unnecessarily
# (though for local DB it might need them)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider external \
  --external-db-host "db.external.com" \
  --external-db-name "printfarmer"
# Expected: Connection string in .env.microservices contains host/db but check credential handling
```
- **Rationale**: Connection strings in env files should be carefully reviewed
- **TDD Approach**: Parse connection string, verify format
- **Complexity**: MEDIUM
- **Risk if Missing**: Credentials in wrong places

---

#### 4.2 Port Management & Conflict Resolution

**Test: `test_port_conflict_resolution`**
```bash
# Verify script finds alternative ports when default ports are in use
# Simulate port 3000 is in use (frontend default)
lsof -i :3000  # Check if occupied
if [ $? -eq 0 ]; then
    # Port is in use
    $DEPLOY_SCRIPT --dry-run --batch --architecture microservices
    # Expected: Detects conflict, proposes alternative port, or shows error
fi
```
- **Rationale**: Port conflicts are common, need graceful handling
- **TDD Approach**: Mock port occupation, verify resolution
- **Complexity**: HIGH (requires mock/simulation)
- **Risk if Missing**: Deployment fails with confusing error

---

**Test: `test_port_range_validation`**
```bash
# Verify ports are in valid range (1-65535)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --http-port 99999  # Invalid
# Expected: Error about invalid port
```
- **Rationale**: Invalid ports cause confusing errors
- **TDD Approach**: Test invalid ports
- **Complexity**: LOW
- **Risk if Missing**: Broken port configuration

---

**Test: `test_port_binding_to_specific_interface`**
```bash
# Verify API binds to appropriate interface for the selected architecture
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: API configured to listen on configured API_PORT and accessible via the service network or host gateway as configured
```
- **Rationale**: Correct API binding ensures clients can reach the service
- **TDD Approach**: Check generated config bindings
- **Complexity**: MEDIUM
- **Risk if Missing**: API not reachable from clients

---

#### 4.3 Network Configuration

**Test: `test_bridge_network_dns_resolution`**
```bash
# Verify microservices can resolve each other via service names
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: Compose file uses Docker DNS (service names resolvable)
# Expected: No hardcoded IPs for inter-service communication
```
- **Rationale**: Service discovery is critical for microservices
- **TDD Approach**: Assert service name references in compose
- **Complexity**: MEDIUM
- **Risk if Missing**: Services can't communicate

---

**Test: `test_network_isolation_microservices`**
```bash
# Verify microservices are on isolated network (not host network)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: network_mode NOT set to "host"
# Expected: Services use Docker bridge network
```
- **Rationale**: Network isolation is security feature
- **TDD Approach**: Assert network_mode is not "host"
- **Complexity**: LOW
- **Risk if Missing**: Services might be exposed on host network

---

**Test: `test_external_database_connectivity`**
```bash
# Verify external database configuration with proper connection validation
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --db-provider external \
  --external-db-host "invalid-host.local"
# Expected: Validation attempt or warning about connectivity
```
- **Rationale**: External DB misconfiguration is common failure point
- **TDD Approach**: Attempt connection, verify error handling
- **Complexity**: HIGH (requires connectivity test)
- **Risk if Missing**: Deployment fails mysteriously on startup

---

#### 4.4 Database Initialization

**Test: `test_database_initialization_order`**
```bash
# Verify database service starts and is healthy before API
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider postgres
# Expected: docker-compose.yml shows api depends_on database
# Expected: Healthcheck configured for database service
```
- **Rationale**: Service start order is critical
- **TDD Approach**: Parse compose, verify dependencies and healthchecks
- **Complexity**: MEDIUM
- **Risk if Missing**: API starts before DB, startup fails

---

**Test: `test_database_healthcheck_configuration`**
```bash
# Verify each database provider has appropriate healthcheck
for provider in postgres sqlserver mysql; do
    $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider $provider
    grep -A5 "healthcheck:" docker-compose.yml
    # Expected: Provider-specific healthcheck present
    # Expected: Appropriate timeout and retry policy
done
```
- **Rationale**: Healthchecks prevent partial failure scenarios
- **TDD Approach**: Assert healthcheck for each provider
- **Complexity**: MEDIUM
- **Risk if Missing**: Services appear healthy when actually broken

---

**Test: `test_database_volume_mount_correctness`**
```bash
# Verify database volume mounted at correct path
for provider in postgres sqlserver mysql; do
    $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider $provider
    # Expected: Volume mounted at correct container path for each provider
    # postgres: /var/lib/postgresql/data
    # sqlserver: /var/opt/mssql
    # mysql: /var/lib/mysql
done
```
- **Rationale**: Wrong mount paths lose data
- **TDD Approach**: Assert mount paths per provider
- **Complexity**: MEDIUM
- **Risk if Missing**: Data loss due to wrong mount path

---

**Test: `test_external_database_no_container_volume`**
```bash
# Verify external DB mode doesn't create database container/volume
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --db-provider external \
  --external-db-host "prod-db.company.com"
grep "database:" docker-compose.yml
# Expected: No database service defined
# Expected: No volume for database
```
- **Rationale**: External DB shouldn't create local database
- **TDD Approach**: Assert no local database service
- **Complexity**: LOW
- **Risk if Missing**: Accidental local database creates confusion

---

#### 4.5 Environment Variable Handling

**Test: `test_env_file_sourcing_order`**
```bash
# Verify environment variables load in correct precedence:
# 1. .env (defaults)
# 2. .env.microservices / .env.monolithic (architecture-specific)
# 3. Command line --env flags
cat > /tmp/test.env << EOF
API_PORT=5000
EOF
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --config-file .deploy-config \
  --env API_PORT=5245 \
  --env DB_PROVIDER=postgres
# Expected: --env flags override .env.microservices
```
- **Rationale**: Precedence confusion causes hard-to-debug issues
- **TDD Approach**: Set conflicting vars at different levels, verify precedence
- **Complexity**: MEDIUM
- **Risk if Missing**: Environment variable precedence bugs

---

**Test: `test_special_characters_in_env_values`**
```bash
# Verify env values with special chars are handled correctly
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --env EXTERNAL_DB_PASSWORD='P@ssw0rd!#$%'
# Expected: Password properly escaped in .env file
# Expected: Docker compose can parse it without errors
```
- **Rationale**: Special chars in passwords/strings cause parsing errors
- **TDD Approach**: Use complex values, verify parsing works
- **Complexity**: MEDIUM
- **Risk if Missing**: Deployment fails with special characters

---

**Test: `test_env_file_encoding_utf8`**
```bash
# Verify .env file is UTF-8 encoded
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices \
  --env APP_DESCRIPTION="PrintFarmer (日本語テスト)"
file .env.microservices | grep -i utf
# Expected: UTF-8 encoding indicated
```
- **Rationale**: Encoding issues cause silent failures
- **TDD Approach**: Check file encoding
- **Complexity**: LOW
- **Risk if Missing**: Unicode values cause parsing errors

---

#### 4.6 React Frontend Configuration

**Test: `test_react_env_production_generated`**
```bash
# Verify .env.production is generated for React build
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
ls -la .env.production
# Expected: .env.production exists
# Expected: Contains API_BASE_URL pointing to API_PORT
```
- **Rationale**: Missing React env causes frontend-to-API communication failure
- **TDD Approach**: Check file exists and has correct values
- **Complexity**: LOW
- **Risk if Missing**: Frontend can't communicate with API

---

**Test: `test_react_cors_configuration`**
```bash
# Verify CORS settings allow frontend-API communication
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
grep "CORS_ORIGINS" .env.microservices
# Expected: Includes API_PORT and HTTP_PORT
# Expected: Format correct for ASP.NET CORS
```
- **Rationale**: CORS misconfiguration prevents frontend-API communication
- **TDD Approach**: Assert CORS includes both frontend and API ports
- **Complexity**: MEDIUM
- **Risk if Missing**: Frontend gets CORS errors

---

#### 4.7 Docker Build Configuration

**Test: `test_multistage_build_targets_exist`**
```bash
# Verify all required multistage targets are defined
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: Dockerfile has targets: api-runtime, frontend-runtime, api-build, frontend-build
# Expected: Each target produces correct image
```
- **Rationale**: Missing targets cause build failures
- **TDD Approach**: Parse Dockerfile, assert targets present
- **Complexity**: MEDIUM
- **Risk if Missing**: Build fails with cryptic errors

---

**Test: `test_build_cache_efficiency`**
```bash
# Verify Dockerfile layer ordering for cache efficiency
# Layers that change frequently should be late
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: Dependencies installed before source code copied
# Expected: Version files early for cache invalidation
```
- **Rationale**: Poor cache efficiency makes builds slow
- **TDD Approach**: Inspect Dockerfile layer order
- **Complexity**: HIGH
- **Risk if Missing**: Slow rebuilds frustrate developers

---

**Test: `test_docker_build_args_passed_correctly`**
```bash
# Verify build args are passed to docker build command
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: docker-compose build passes BUILDKIT_INLINE_CACHE=1 for cache efficiency
# Expected: Build args for Node.js version, .NET version, etc.
```
- **Rationale**: Build args control build behavior and dependencies
- **TDD Approach**: Capture docker compose build command, verify args
- **Complexity**: MEDIUM
- **Risk if Missing**: Build uses wrong versions

---

#### 4.8 Addon Stack Deployment

**Test: `test_prometheus_scrape_config_generated`**
```bash
# Verify Prometheus configuration includes API service
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --include-monitoring
cat deploy/monitoring/prometheus.yml
# Expected: Job for scraping API metrics
# Expected: Correct scrape endpoints
```
- **Rationale**: Monitoring misconfiguration means metrics aren't collected
- **TDD Approach**: Check prometheus config
- **Complexity**: HIGH
- **Risk if Missing**: Monitoring doesn't work

---

**Test: `test_grafana_datasource_configuration`**
```bash
# Verify Grafana datasources are configured
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --include-monitoring
# Expected: Prometheus datasource configured
# Expected: Loki datasource configured (if telemetry included)
```
- **Rationale**: Unconfigured datasources mean Grafana dashboards fail
- **TDD Approach**: Check Grafana provisioning configs
- **Complexity**: HIGH
- **Risk if Missing**: Grafana dashboards blank

---

**Test: `test_security_addon_certificate_mounting`**
```bash
# Verify security addon properly mounts certificates
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --include-security
# Expected: Certificate volumes mounted
# Expected: Certificate paths configured in nginx/app
```
- **Rationale**: Wrong certificate mounting breaks TLS
- **TDD Approach**: Verify cert mounts
- **Complexity**: HIGH
- **Risk if Missing**: TLS doesn't work, security failure

---

#### 4.9 Teardown & Cleanup

**Test: `test_teardown_removes_all_containers`**
```bash
# Verify teardown completely removes deployment
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
docker ps -a | grep printfarmer
containers_before=$?

$DEPLOY_SCRIPT --teardown-deployment

docker ps -a | grep printfarmer
containers_after=$?

# Expected: No printfarmer containers after teardown
```
- **Rationale**: Incomplete cleanup causes conflicts on redeploy
- **TDD Approach**: Count containers before/after
- **Complexity**: HIGH (requires actual Docker)
- **Risk if Missing**: Redeploy conflicts

---

**Test: `test_cleanup_generated_preserves_important_files`**
```bash
# Verify --cleanup-generated removes temp files but keeps important ones
$DEPLOY_SCRIPT --batch --architecture microservices --cleanup-generated
# Expected: docker-compose.yml removed
# Expected: Dockerfile.multistage removed
# Expected: BUT: .env files, .deploy-config preserved for rollback
```
- **Rationale**: Cleanup shouldn't remove files needed for rollback
- **TDD Approach**: Check which files removed vs preserved
- **Complexity**: MEDIUM
- **Risk if Missing**: Rollback impossible

---

#### 4.10 Validation & Health Checks

**Test: `test_api_health_check_on_startup`**
```bash
# Verify API health check works after deployment
# (Would require actual deployment, not just dry-run)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: Health check endpoint configured: /healthz, /health
# Expected: Appropriate timeouts and retry counts
```
- **Rationale**: Health checks determine successful deployment
- **TDD Approach**: Verify health endpoint configured
- **Complexity**: MEDIUM
- **Risk if Missing**: Deployment incomplete but appears successful

---

**Test: `test_database_connectivity_validation`**
```bash
# Verify API can connect to database after startup
# (Would require actual deployment)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --db-provider postgres
# Expected: Connection string test command configured
# Expected: Retry logic for initial startup delays
```
- **Rationale**: Database connectivity failures aren't detected
- **TDD Approach**: Verify connectivity check implemented
- **Complexity**: HIGH
- **Risk if Missing**: Silent database connection failures

---

**Test: `test_signalr_hub_availability`**
```bash
# Verify SignalR hub is accessible after deployment
# (Would require actual deployment)
$DEPLOY_SCRIPT --dry-run --batch --architecture microservices
# Expected: SignalR hub endpoint configured
# Expected: CORS configured for hub
```
- **Rationale**: SignalR hub is critical for real-time printer updates
- **TDD Approach**: Verify hub configuration
- **Complexity**: MEDIUM
- **Risk if Missing**: Real-time updates don't work

---

#### 4.11 Logging & Diagnostics

**Test: `test_audit_log_created_with_deployment_config`**
```bash
# Verify deployment creates audit log of configuration
$DEPLOY_SCRIPT --batch --architecture microservices
# Expected: Audit log file exists with timestamp
# Expected: Contains deployment architecture, database provider, etc.
# Expected: Does NOT contain passwords (masked)
```
- **Rationale**: Audit logging is critical for troubleshooting
- **TDD Approach**: Check audit log exists and contents
- **Complexity**: MEDIUM
- **Risk if Missing**: Can't troubleshoot past deployments

---

**Test: `test_diagnostic_command_output_helpful`**
```bash
# Verify diagnostic output provides useful debugging info
$DEPLOY_SCRIPT --batch --architecture microservices
$DEPLOY_SCRIPT --verify-deployment
# Expected: Clear output showing each service status
# Expected: Includes connection string details (masked)
# Expected: Includes port information
```
- **Rationale**: Poor diagnostics make troubleshooting hard
- **TDD Approach**: Run verification, check output quality
- **Complexity**: MEDIUM
- **Risk if Missing**: Troubleshooting impossible

---

---

## 5. Test Implementation Priority Matrix

### High Priority (Implement Now - Prevent Critical Failures)

| Test | Severity | Effort | Impact |
|------|----------|--------|--------|
| `test_password_not_logged_to_stdout` | CRITICAL | HIGH | Security breach prevention |
| `test_generated_compose_file_is_valid_yaml` | CRITICAL | LOW | Prevents deploy failures |
| `test_database_initialization_order` | CRITICAL | MEDIUM | Startup failure prevention |
| `test_database_volume_mount_correctness` | CRITICAL | MEDIUM | Data loss prevention |
| `test_host_network_localhost_binding` | HIGH | MEDIUM | Host-network functionality |
| `test_missing_required_architecture_argument` | HIGH | LOW | User error prevention |
| `test_invalid_database_provider` | HIGH | LOW | Configuration validation |
| `test_orcaslicer_worker_count_validation` | HIGH | MEDIUM | Feature validation |

### Medium Priority (Implement Next - Quality Improvements)

| Test | Severity | Effort | Impact |
|------|----------|--------|--------|
| `test_permission_denied_output_directory` | MEDIUM | MEDIUM | Error handling |
| `test_port_conflict_resolution` | MEDIUM | HIGH | Usability |
| `test_react_cors_configuration` | MEDIUM | MEDIUM | Frontend functionality |
| `test_addon_services_dont_conflict_with_core` | MEDIUM | MEDIUM | Addon stability |
| `test_connection_string_does_not_contain_credentials` | MEDIUM | LOW | Security audit |
| `test_password_complexity_requirements` | MEDIUM | MEDIUM | Security standards |

### Low Priority (Nice to Have - Edge Cases)

| Test | Severity | Effort | Impact |
|------|----------|--------|--------|
| `test_special_characters_in_env_values` | LOW | MEDIUM | Edge case handling |
| `test_elasticsearch_stack_availability_by_architecture` | LOW | HIGH | Architecture support |
| `test_audit_log_created_with_deployment_config` | LOW | MEDIUM | Diagnostics |
| `test_env_file_encoding_utf8` | LOW | LOW | Edge case |

---

## 6. TDD Workflow Recommendations

### For Future Features: Always Follow This Workflow

1. **Write Tests First** (RED phase)
   ```bash
   # Before implementing feature, write failing test
   # Test should describe desired behavior clearly
   # Include both happy path and error cases
   
   # Example: Adding new addon stack
   # Write test_new_addon_stack_deployment() FIRST
   # Verify it fails: `bash tests/test-compose-generator.sh | grep FAIL`
   ```

2. **Implement Feature** (GREEN phase)
   ```bash
   # Now implement feature in compose-generator.sh
   # Commit with message: "Add new addon stack support (TDD)"
   # Verify test passes: `bash tests/test-compose-generator.sh | grep "new_addon_stack"` PASS
   ```

3. **Refactor & Optimize** (REFACTOR phase)
   ```bash
   # Clean up code while keeping tests green
   # All tests still passing: `bash tests/test-compose-generator.sh`
   ```

4. **Update Documentation**
   ```bash
   # Document new feature with examples
   # Update this TEST_COVERAGE_ANALYSIS.md
   # Move test from "Missing" to "Current Tests"
   ```

---

## 7. Test Maintenance Checklist

Before merging any changes to `deploy-docker.sh` or `compose-generator.sh`:

- [ ] All existing tests still pass: `bash tests/test-compose-generator.sh && bash tests/test-deploy-docker.sh`
- [ ] New tests added for new features (TDD: tests before code)
- [ ] Tests cover both success and error cases
- [ ] Test names clearly describe what they validate
- [ ] Tests are independent and can run in any order
- [ ] Tests clean up after themselves (temp files removed)
- [ ] No hardcoded paths or assumptions about OS
- [ ] Tests run in < 5 seconds (except integration tests)
- [ ] Documentation updated with test changes
- [ ] Audit log shows what changed and why

---

## 8. Continuous Integration (CI) Recommendations

### Pre-commit Hook

```bash
#!/bin/bash
# .git/hooks/pre-commit
cd scripts/docker || exit 1
if [ -f compose-generator.sh ] || [ -f ../../scripts/deploy-docker.sh ]; then
    echo "Running deployment script tests..."
    timeout 900 bash ../../tests/test-compose-generator.sh || exit 1
    timeout 900 bash ../../tests/test-deploy-docker.sh || exit 1
fi
exit 0
```

### CI/CD Pipeline (GitHub Actions / GitLab CI)

```yaml
# Run all tests when deploy scripts change
deploy_tests:
  trigger: always  # Or when path changes
  timeout: 15m
  script:
    - bash tests/test-compose-generator.sh
    - bash tests/test-deploy-docker.sh
```

---

## 9. Summary Statistics

| Metric | Current | Target | Gap |
|--------|---------|--------|-----|
| Compose-generator tests | 20 | 32 | 12 ❌ |
| Deploy-docker tests | 24 | 49 | 25 ❌ |
| **Total tests** | **44** | **81** | **37 ❌** |
| Test pass rate | 100% ✅ | 100% | - |
| Coverage: Core features | 95% ✅ | 100% | - |
| Coverage: Error cases | 40% ❌ | 100% | 60% gap |
| Coverage: Edge cases | 20% ❌ | 80% | 60% gap |

---

## 10. References

- **Test Files**: `/tests/test-compose-generator.sh`, `/tests/test-deploy-docker.sh`
- **Test Framework**: `/tests/test-framework.sh`
- **Scripts Under Test**: 
  - `/scripts/docker/compose-generator.sh` (925 lines)
  - `/scripts/deploy-docker.sh` (4376 lines)
- **Related Docs**: `/REACT_MIGRATION_README.md`, `/DEPLOYMENT_OVERVIEW.md`

---

**Last Updated**: November 1, 2025  
**Status**: Analysis Complete - Ready for Implementation  
**Recommended Next Steps**:
1. Prioritize high-priority tests (see section 5)
2. Implement critical tests first (password logging, YAML validation, DB mount paths)
3. Add remaining tests as features evolve
4. Maintain TDD discipline for all future feature development
