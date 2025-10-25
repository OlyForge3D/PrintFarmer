#!/bin/bash

# run-tests.sh - Test runner for PrintFarmer deployment scripts
# Executes all test suites and provides summary

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Test results
TOTAL_SUITES=0
PASSED_SUITES=0
FAILED_SUITES=0

log_info() { echo -e "${BLUE}[INFO]${NC} $1" >&2; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1" >&2; }
log_error() { echo -e "${RED}[ERROR]${NC} $1" >&2; }
log_warning() { echo -e "${YELLOW}[WARNING]${NC} $1" >&2; }

show_usage() {
    cat << EOF
Usage: $0 [OPTIONS]

Run test suites for PrintFarmer deployment scripts.

OPTIONS:
    -h, --help              Show this help message
    -v, --verbose           Show verbose output from tests
    -f, --fast              Skip slower integration tests
    -s, --suite SUITE       Run specific test suite only
                           (compose-generator|deploy-docker|integration)
    --parallel              Run test suites in parallel (experimental)

EXAMPLES:
    # Run all tests
    $0
    
    # Run only compose generator tests
    $0 --suite compose-generator
    
    # Run tests with verbose output
    $0 --verbose

EOF
}

run_test_suite() {
    local test_script="$1"
    local suite_name="$2"
    local verbose="${3:-false}"
    
    TOTAL_SUITES=$((TOTAL_SUITES + 1))
    
    log_info "Running test suite: $suite_name"
    echo "=================================================="
    
    if [ "$verbose" = "true" ]; then
        if bash "$test_script"; then
            PASSED_SUITES=$((PASSED_SUITES + 1))
            log_success "✓ $suite_name passed"
        else
            FAILED_SUITES=$((FAILED_SUITES + 1))
            log_error "✗ $suite_name failed"
        fi
    else
        local output
        if output=$(bash "$test_script" 2>&1); then
            PASSED_SUITES=$((PASSED_SUITES + 1))
            log_success "✓ $suite_name passed"
        else
            FAILED_SUITES=$((FAILED_SUITES + 1))
            log_error "✗ $suite_name failed"
            echo "$output"
        fi
    fi
    
    echo
}

check_dependencies() {
    log_info "Checking test dependencies..."
    
    # Check for required commands
    local missing_deps=()
    
    if ! command -v timeout >/dev/null 2>&1; then
        missing_deps+=("timeout")
    fi
    
    if ! command -v mktemp >/dev/null 2>&1; then
        missing_deps+=("mktemp")
    fi
    
    if [ ${#missing_deps[@]} -gt 0 ]; then
        log_error "Missing required dependencies: ${missing_deps[*]}"
        log_error "Please install the missing commands and try again"
        return 1
    fi
    
    # Check that deployment scripts exist
    if [ ! -f "$REPO_ROOT/scripts/deploy-docker.sh" ]; then
        log_error "Deploy script not found: $REPO_ROOT/scripts/deploy-docker.sh"
        return 1
    fi
    
    if [ ! -f "$REPO_ROOT/scripts/docker/compose-generator.sh" ]; then
        log_error "Compose generator not found: $REPO_ROOT/scripts/docker/compose-generator.sh"
        return 1
    fi
    
    # Make sure scripts are executable
    chmod +x "$REPO_ROOT/scripts/deploy-docker.sh"
    chmod +x "$REPO_ROOT/scripts/docker/compose-generator.sh"
    chmod +x "$SCRIPT_DIR"/*.sh
    
    log_success "All dependencies satisfied"
    return 0
}

main() {
    local verbose=false
    local fast=false
    local specific_suite=""
    local parallel=false
    
    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            -h|--help)
                show_usage
                exit 0
                ;;
            -v|--verbose)
                verbose=true
                shift
                ;;
            -f|--fast)
                fast=true
                shift
                ;;
            -s|--suite)
                specific_suite="$2"
                shift 2
                ;;
            --parallel)
                parallel=true
                shift
                ;;
            *)
                log_error "Unknown option: $1"
                show_usage
                exit 1
                ;;
        esac
    done
    
    echo
    echo "=================================================="
    echo "🧪 PrintFarmer Deployment Script Test Suite"
    echo "=================================================="
    echo
    
    # Check dependencies
    if ! check_dependencies; then
        exit 1
    fi
    
    echo
    
    # Reset counters
    TOTAL_SUITES=0
    PASSED_SUITES=0
    FAILED_SUITES=0
    
    # Run test suites
    case "$specific_suite" in
        "")
            # Run all suites
            run_test_suite "$SCRIPT_DIR/test-compose-generator.sh" "Compose Generator Tests" "$verbose"
            run_test_suite "$SCRIPT_DIR/test-deploy-docker.sh" "Deploy Docker Tests" "$verbose"
            
            if [ "$fast" != "true" ]; then
                run_test_suite "$SCRIPT_DIR/test-integration.sh" "Integration Tests" "$verbose"
            fi
            ;;
        "compose-generator")
            run_test_suite "$SCRIPT_DIR/test-compose-generator.sh" "Compose Generator Tests" "$verbose"
            ;;
        "deploy-docker")
            run_test_suite "$SCRIPT_DIR/test-deploy-docker.sh" "Deploy Docker Tests" "$verbose"
            ;;
        "integration")
            run_test_suite "$SCRIPT_DIR/test-integration.sh" "Integration Tests" "$verbose"
            ;;
        *)
            log_error "Unknown test suite: $specific_suite"
            log_error "Available suites: compose-generator, deploy-docker, integration"
            exit 1
            ;;
    esac
    
    # Print summary
    echo "=================================================="
    echo "📊 Test Summary"
    echo "=================================================="
    
    if [ $FAILED_SUITES -eq 0 ]; then
        log_success "All test suites passed! ($PASSED_SUITES/$TOTAL_SUITES)"
        echo
        log_info "✅ Deployment scripts are working correctly"
        log_info "✅ Multi-stage Docker builds configured properly"
        log_info "✅ Redis and PrusaSlicer references removed"
        log_info "✅ Configuration generation and validation working"
        exit 0
    else
        log_error "Test suites failed: $FAILED_SUITES, Passed: $PASSED_SUITES, Total: $TOTAL_SUITES"
        echo
        log_warning "❌ Some deployment functionality may not work correctly"
        log_warning "❌ Review test failures above and fix issues"
        exit 1
    fi
}

# Handle script interruption
trap 'echo; log_warning "Tests interrupted by user"; exit 130' INT TERM

# Run main function
main "$@"