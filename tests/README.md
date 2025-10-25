# PrintFarmer Deployment Script Tests

This directory contains comprehensive test coverage for the PrintFarmer deployment scripts, including the Docker Compose generator and main deployment script.

## Overview

The test suite ensures that:
- ✅ **Multi-stage Docker builds** work correctly across all architectures
- ✅ **Redis references** have been completely removed
- ✅ **PrusaSlicer references** have been completely removed  
- ✅ **Configuration generation** works for all deployment types
- ✅ **Argument parsing and validation** functions correctly
- ✅ **Integration between scripts** works seamlessly

## Test Structure

```
tests/
├── run-tests.sh              # Main test runner
├── test-framework.sh         # Bash testing framework
├── test-compose-generator.sh # Compose generator tests
├── test-deploy-docker.sh     # Main deployment script tests
├── test-integration.sh       # End-to-end integration tests
├── fixtures/                 # Test data and fixtures
└── README.md                 # This file
```

## Running Tests

### Quick Start

```bash
# Run all tests
./tests/run-tests.sh

# Run with verbose output
./tests/run-tests.sh --verbose

# Run specific test suite only
./tests/run-tests.sh --suite compose-generator
```

### Test Suites

#### 1. Compose Generator Tests (`test-compose-generator.sh`)
Tests the `scripts/docker/compose-generator.sh` script:

- ✅ **Architecture validation** - Ensures only valid architectures are accepted
- ✅ **File generation** - Verifies Docker Compose files are created correctly
- ✅ **Multi-stage builds** - Confirms all services use `Dockerfile.multistage`
- ✅ **Target specification** - Checks correct build targets are specified
- ✅ **Redis removal** - Ensures no Redis services or references
- ✅ **PrusaSlicer removal** - Ensures no PrusaSlicer workers or references
- ✅ **Configuration options** - Tests worker counts, database providers, etc.

#### 2. Deploy Script Tests (`test-deploy-docker.sh`)
Tests the main `scripts/deploy-docker.sh` script:

- ✅ **Help output** - Verifies help text is correct and complete
- ✅ **Architecture options** - Confirms only `monolithic|microservices|host-network`
- ✅ **Batch mode** - Tests non-interactive execution
- ✅ **Dry-run mode** - Ensures validation without deployment
- ✅ **Configuration validation** - Tests port conflicts, worker counts, etc.
- ✅ **Environment variables** - Tests configuration via env vars
- ✅ **No Redis prompts** - Ensures Redis configuration is not requested
- ✅ **No PrusaSlicer prompts** - Ensures PrusaSlicer configuration is not requested

#### 3. Configuration Persistence Tests (`test-config-persistence.sh`)
Tests configuration saving and loading for monitoring/telemetry/security settings:

- ✅ **Configuration persistence** - Verifies monitoring settings are saved to config file
- ✅ **Configuration loading** - Ensures saved settings are properly loaded on subsequent runs
- ✅ **CLI flag override** - Confirms command-line flags override saved configuration
- ✅ **Interactive choice memory** - Tests that user choices in interactive mode are remembered

#### 4. Integration Tests (`test-integration.sh`)
Tests the complete deployment pipeline:

- ✅ **End-to-end workflows** - Full deployment pipeline for each architecture
- ✅ **Configuration consistency** - Both scripts generate compatible outputs
- ✅ **File coordination** - Proper `Dockerfile.multistage` handling
- ✅ **Pipeline validation** - Complete dry-run deployments work
- ✅ **Cleanup and regeneration** - Configuration changes work correctly

## Test Framework

The test suite uses a custom bash testing framework (`test-framework.sh`) that provides:

### Assertion Functions
- `assert_equals` - Check values are equal
- `assert_contains` - Check string contains substring
- `assert_file_exists` - Check file exists
- `assert_command_success` - Check command succeeds
- `assert_exit_code` - Check specific exit codes

### Test Management
- `start_test` - Begin a test case
- `pass_test` - Mark test as passed
- `fail_test` - Mark test as failed
- `run_test_suite` - Execute and summarize test suite

### Utilities
- `capture_output` - Capture command output for testing
- `create_test_temp_dir` - Create isolated test environment
- `setup_test_environment` - Configure test-specific settings

## Requirements

The tests require:
- **Bash 4.0+** - For script execution
- **timeout** command - For preventing hanging tests
- **mktemp** command - For temporary directories
- **Standard Unix tools** - grep, cat, etc.

Tests are designed to be:
- ⚡ **Fast** - Most tests complete in seconds
- 🔒 **Isolated** - Each test uses separate temporary directories
- 🛡️ **Safe** - All tests run in dry-run mode, no actual deployments
- 📋 **Comprehensive** - Cover all major functionality and edge cases

## Key Test Scenarios

### Multi-Stage Build Verification
```bash
# Verifies all architectures use Dockerfile.multistage
assert_contains "$compose_content" "dockerfile: Dockerfile.multistage"
assert_contains "$compose_content" "target: api-runtime"
assert_contains "$compose_content" "target: frontend-runtime"
```

### Redis Removal Verification
```bash
# Ensures no Redis references anywhere
assert_not_contains "$output" "Redis"
assert_not_contains "$compose_content" "redis:"
assert_not_contains "$compose_content" "ConnectionStrings__Redis"
```

### PrusaSlicer Removal Verification
```bash
# Ensures no PrusaSlicer references anywhere
assert_not_contains "$output" "PrusaSlicer"
assert_not_contains "$compose_content" "prusaslicer-worker"
assert_not_contains "$compose_content" "PrusaSlicerPath"
```

### Architecture Validation
```bash
# Ensures only valid architectures accepted
assert_exit_code 1 "$DEPLOY_SCRIPT --architecture invalid"
assert_contains "$help_output" "monolithic|microservices|host-network"
assert_not_contains "$help_output" "multistage"
```

## Running Individual Tests

```bash
# Run compose generator tests only
./tests/test-compose-generator.sh

# Run deploy script tests only  
./tests/test-deploy-docker.sh

# Run configuration persistence tests only
./tests/test-config-persistence.sh

# Run integration tests only
./tests/test-integration.sh
```

## Troubleshooting

### Test Failures
If tests fail, check:
1. **Script permissions** - Ensure deployment scripts are executable
2. **Dependencies** - Verify all required commands are available
3. **Environment** - Tests should run from repository root
4. **Timeouts** - Increase timeout values if system is slow

### Verbose Output
For detailed test output:
```bash
./tests/run-tests.sh --verbose
```

### Fast Testing
To skip slower integration tests:
```bash
./tests/run-tests.sh --fast
```

## Contributing

When adding new deployment features:

1. **Add tests first** - Write tests for new functionality
2. **Update existing tests** - Modify tests if behavior changes
3. **Test all architectures** - Ensure monolithic, microservices, and host-network work
4. **Verify removal** - Ensure Redis and PrusaSlicer remain removed
5. **Run full suite** - Execute all tests before submitting changes

## Test Coverage

Current test coverage includes:

### Compose Generator (`scripts/docker/compose-generator.sh`)
- ✅ 14 test cases covering all major functionality
- ✅ Architecture validation and file generation
- ✅ Multi-stage build integration
- ✅ Worker configuration options
- ✅ Database provider selection
- ✅ Monitoring and telemetry options

### Deploy Script (`scripts/deploy-docker.sh`) 
- ✅ 16 test cases covering deployment pipeline
- ✅ Command-line argument parsing
- ✅ Configuration validation and persistence
- ✅ Environment variable handling
- ✅ Port conflict detection
- ✅ Batch and dry-run modes

### Configuration Persistence (`test-config-persistence.sh`)
- ✅ 3 test cases covering configuration memory
- ✅ Monitoring/telemetry/security setting persistence
- ✅ CLI flag override behavior
- ✅ Configuration loading and display

### Integration Tests
- ✅ 10 test cases covering end-to-end workflows
- ✅ Complete deployment pipelines for all architectures
- ✅ Configuration consistency between scripts
- ✅ File coordination and cleanup
- ✅ Error handling and recovery

**Total: 43+ test cases ensuring robust deployment functionality**