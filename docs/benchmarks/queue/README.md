# Queue Provider Benchmarks

This directory contains benchmark scripts and results for evaluating different queue providers for PrintFarmer's slicer microservices architecture.

## Queue Providers Evaluated

1. **Redis Streams** (Current Implementation)
2. **RabbitMQ**
3. **Apache Kafka** (if applicable for workload)

## Benchmark Scenarios

### Small Load
- 10-100 jobs/minute
- Small job payloads (1-5KB)
- Single worker processing

### Medium Load  
- 100-1000 jobs/minute
- Medium job payloads (10-50KB)
- 2-5 workers processing

### Burst Load
- 1000+ jobs/minute sustained
- Large job payloads (100KB+)
- 5-10 workers processing

## Metrics Collected

- **Latency**: P50, P95, P99 for enqueue/dequeue operations
- **Throughput**: Jobs processed per second
- **Reliability**: Message delivery guarantees
- **Resource Usage**: CPU, memory, network
- **Operational Complexity**: Setup, monitoring, maintenance

## Directory Structure

```
queue/
├── README.md                   # This file
├── benchmark-runner.sh         # Main benchmark execution script
├── results/                    # Benchmark results and raw data
│   ├── redis-streams/         
│   ├── rabbitmq/              
│   └── kafka/                 
├── src/                       # Benchmark application source
│   ├── QueueBenchmark.csproj  # Benchmark application
│   ├── Program.cs             # Main benchmark runner
│   ├── Providers/             # Queue provider implementations
│   └── Models/                # Benchmark models and config
└── docker/                    # Docker setup for queue providers
    ├── redis.yml
    ├── rabbitmq.yml
    └── kafka.yml
```

## Running Benchmarks

```bash
# Start infrastructure
./benchmark-runner.sh setup

# Run all benchmarks
./benchmark-runner.sh run-all

# Run specific provider
./benchmark-runner.sh run redis-streams
./benchmark-runner.sh run rabbitmq
./benchmark-runner.sh run kafka

# Generate reports
./benchmark-runner.sh report
```

## Results

See individual provider directories under `results/` for detailed benchmark data and analysis.