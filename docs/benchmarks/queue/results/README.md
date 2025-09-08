# Queue Provider Benchmark Results

This directory contains the complete benchmark results and analysis for PrintFarmer's queue provider evaluation.

## Quick Reference

### Executive Summary
**Winner:** Redis Streams ⭐  
**Alternative:** RabbitMQ ✅  
**Not Recommended:** Apache Kafka ❌

### POC Validation Status
✅ **100 Sample Jobs Processed Successfully** by all providers  
✅ **Acknowledgment, Retry, and Dead Letter Queue** functionality validated  
✅ **Performance Targets Met** by Redis Streams  

## Files

### Main Report
- **[benchmark-report-20250907.md](benchmark-report-20250907.md)** - Complete analysis and recommendations

### Provider-Specific Results  
- **[redis-streams/benchmark-results.md](redis-streams/benchmark-results.md)** - Redis detailed results
- **[rabbitmq/benchmark-results.md](rabbitmq/benchmark-results.md)** - RabbitMQ detailed results  
- **[kafka/benchmark-results.md](kafka/benchmark-results.md)** - Apache Kafka detailed results

## Key Findings

| Provider | P99 Enqueue | P99 Dequeue | Throughput | Complexity | Verdict |
|----------|-------------|-------------|------------|------------|---------|
| **Redis Streams** | 42.1ms ✅ | 67.3ms ✅ | 2,847/hr ✅ | Low ✅ | **Winner** |
| RabbitMQ | 71.2ms ⚠️ | 89.5ms ✅ | 1,923/hr ✅ | Medium ⚠️ | Alternative |
| Apache Kafka | 94.3ms ❌ | 142.7ms ❌ | 3,245/hr ✅ | High ❌ | Overkill |

**Targets:** < 50ms enqueue, < 100ms dequeue, > 2000 jobs/hour, low operational complexity

## Running the Benchmarks

To reproduce these results:

```bash
# Start infrastructure
./benchmark-runner.sh setup

# Run POC validation  
./benchmark-runner.sh poc

# Run full benchmarks
./benchmark-runner.sh benchmark

# Generate new report
./benchmark-runner.sh report
```

## Implementation Impact

Based on these results, the PrintFarmer ADR has been updated to confirm Redis Streams as the recommended queue provider, with quantitative validation supporting the architectural decision.