#!/bin/bash

# Queue Provider Benchmark Runner
# Manages infrastructure and executes benchmarks for Redis, RabbitMQ, and Kafka

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOCKER_DIR="$SCRIPT_DIR/docker"
SRC_DIR="$SCRIPT_DIR/src"
RESULTS_DIR="$SCRIPT_DIR/results"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

log() {
    echo -e "${GREEN}[$(date '+%Y-%m-%d %H:%M:%S')]${NC} $1"
}

warn() {
    echo -e "${YELLOW}[$(date '+%Y-%m-%d %H:%M:%S')]${NC} $1"
}

error() {
    echo -e "${RED}[$(date '+%Y-%m-%d %H:%M:%S')]${NC} $1"
}

info() {
    echo -e "${BLUE}[$(date '+%Y-%m-%d %H:%M:%S')]${NC} $1"
}

# Check prerequisites
check_prerequisites() {
    log "Checking prerequisites..."
    
    if ! command -v docker &> /dev/null; then
        error "Docker is not installed or not in PATH"
        exit 1
    fi
    
    if ! command -v docker-compose &> /dev/null; then
        error "Docker Compose is not installed or not in PATH"
        exit 1
    fi
    
    if ! command -v dotnet &> /dev/null; then
        error ".NET SDK is not installed or not in PATH"
        exit 1
    fi
    
    log "Prerequisites check passed"
}

# Setup infrastructure
setup_infrastructure() {
    log "Setting up infrastructure..."
    
    # Create results directories
    mkdir -p "$RESULTS_DIR"/{redis-streams,rabbitmq,kafka}
    
    # Start Redis
    info "Starting Redis..."
    docker-compose -f "$DOCKER_DIR/redis.yml" up -d
    
    # Start RabbitMQ
    info "Starting RabbitMQ..."
    docker-compose -f "$DOCKER_DIR/rabbitmq.yml" up -d
    
    # Start Kafka
    info "Starting Kafka..."
    docker-compose -f "$DOCKER_DIR/kafka.yml" up -d
    
    # Wait for services to be ready
    wait_for_services
    
    log "Infrastructure setup complete"
}

# Wait for services to be healthy
wait_for_services() {
    info "Waiting for services to be ready..."
    
    # Wait for Redis
    info "Waiting for Redis..."
    for i in {1..30}; do
        if docker exec benchmark-redis redis-cli ping &> /dev/null; then
            log "Redis is ready"
            break
        fi
        if [ $i -eq 30 ]; then
            error "Redis failed to start within 30 attempts"
            exit 1
        fi
        sleep 2
    done
    
    # Wait for RabbitMQ
    info "Waiting for RabbitMQ..."
    for i in {1..60}; do
        if docker exec benchmark-rabbitmq rabbitmq-diagnostics ping &> /dev/null; then
            log "RabbitMQ is ready"
            break
        fi
        if [ $i -eq 60 ]; then
            error "RabbitMQ failed to start within 60 attempts"
            exit 1
        fi
        sleep 3
    done
    
    # Wait for Kafka
    info "Waiting for Kafka..."
    for i in {1..60}; do
        if docker exec benchmark-kafka kafka-broker-api-versions --bootstrap-server localhost:9092 &> /dev/null; then
            log "Kafka is ready"
            break
        fi
        if [ $i -eq 60 ]; then
            error "Kafka failed to start within 60 attempts"
            exit 1
        fi
        sleep 3
    done
}

# Build benchmark application
build_benchmark() {
    log "Building benchmark application..."
    
    cd "$SRC_DIR"
    
    # Restore packages
    dotnet restore QueueBenchmark.csproj
    
    # Build
    dotnet build QueueBenchmark.csproj -c Release
    
    log "Benchmark application built successfully"
}

# Run POC demonstration
run_poc() {
    log "Running POC demonstration..."
    
    cd "$SRC_DIR"
    
    # Run the POC
    dotnet run -c Release --no-build
    
    log "POC demonstration completed"
}

# Run formal benchmarks
run_benchmarks() {
    log "Running formal benchmarks..."
    
    cd "$SRC_DIR"
    
    # Run BenchmarkDotNet benchmarks
    dotnet run -c Release --no-build benchmark
    
    # Move results to results directory
    if [ -d "BenchmarkDotNet.Artifacts" ]; then
        cp -r BenchmarkDotNet.Artifacts/results/* "$RESULTS_DIR/"
        log "Benchmark results copied to $RESULTS_DIR"
    fi
    
    log "Formal benchmarks completed"
}

# Generate benchmark report
generate_report() {
    log "Generating benchmark report..."
    
    REPORT_FILE="$RESULTS_DIR/benchmark-report-$(date +%Y%m%d-%H%M%S).md"
    
    cat > "$REPORT_FILE" << EOF
# Queue Provider Benchmark Report

**Generated:** $(date '+%Y-%m-%d %H:%M:%S')
**Duration:** $(date -d@$(($(date +%s) - START_TIME)) -u +%H:%M:%S)

## Executive Summary

This benchmark compared Redis Streams, RabbitMQ, and Apache Kafka for the PrintFarmer slicer microservices queue workload.

## Test Environment

- **OS:** $(uname -s) $(uname -r)
- **CPU:** $(nproc) cores
- **Memory:** $(free -h | grep Mem | awk '{print $2}')
- **.NET Version:** $(dotnet --version)
- **Docker Version:** $(docker --version)

## Infrastructure Versions

- **Redis:** $(docker exec benchmark-redis redis-server --version | head -n 1)
- **RabbitMQ:** $(docker exec benchmark-rabbitmq rabbitmq-diagnostics status | grep "RabbitMQ version" | head -n 1)
- **Kafka:** Latest Confluent Platform image

## Test Scenarios

### POC Results (100 Sample Jobs)
The POC successfully demonstrated:
- Enqueue/dequeue operations
- Acknowledgment mechanisms  
- Retry logic with exponential backoff
- Dead letter queue simulation
- Error handling and recovery

### Performance Benchmarks
See detailed results in the individual provider directories:
- [Redis Results](redis-streams/)
- [RabbitMQ Results](rabbitmq/)
- [Kafka Results](kafka/)

## Key Findings

1. **Latency Performance**: [To be filled based on actual results]
2. **Throughput Performance**: [To be filled based on actual results]
3. **Resource Usage**: [To be filled based on actual results]
4. **Operational Complexity**: [To be filled based on actual results]

## Recommendations

Based on the benchmark results, the recommendation for PrintFarmer's queue provider is:
[To be updated based on actual results]

## Raw Data

Detailed benchmark data and configuration files are available in the respective provider directories.
EOF

    log "Benchmark report generated: $REPORT_FILE"
}

# Cleanup infrastructure
cleanup_infrastructure() {
    log "Cleaning up infrastructure..."
    
    # Stop and remove containers
    docker-compose -f "$DOCKER_DIR/kafka.yml" down -v || true
    docker-compose -f "$DOCKER_DIR/rabbitmq.yml" down -v || true
    docker-compose -f "$DOCKER_DIR/redis.yml" down -v || true
    
    # Clean up any remaining containers
    docker container prune -f || true
    docker volume prune -f || true
    
    log "Infrastructure cleanup complete"
}

# Show usage
show_usage() {
    cat << EOF
Queue Provider Benchmark Runner

Usage: $0 [COMMAND]

Commands:
    setup           Setup infrastructure (Redis, RabbitMQ, Kafka)
    build          Build benchmark application
    poc            Run POC demonstration (100 jobs)
    benchmark      Run formal benchmarks
    run-all        Run complete benchmark suite (setup + build + poc + benchmark)
    report         Generate benchmark report
    cleanup        Stop and cleanup infrastructure
    help           Show this help message

Examples:
    $0 setup                  # Start all queue providers
    $0 poc                   # Run POC with 100 sample jobs
    $0 run-all               # Complete benchmark run
    $0 cleanup               # Stop all services

EOF
}

# Main execution
main() {
    START_TIME=$(date +%s)
    
    case "${1:-help}" in
        setup)
            check_prerequisites
            setup_infrastructure
            ;;
        build)
            build_benchmark
            ;;
        poc)
            run_poc
            ;;
        benchmark)
            run_benchmarks
            ;;
        run-all)
            check_prerequisites
            setup_infrastructure
            build_benchmark
            run_poc
            run_benchmarks
            generate_report
            ;;
        report)
            generate_report
            ;;
        cleanup)
            cleanup_infrastructure
            ;;
        help|*)
            show_usage
            ;;
    esac
}

# Run main function
main "$@"