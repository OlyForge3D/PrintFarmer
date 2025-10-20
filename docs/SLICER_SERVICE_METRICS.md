# Slicer Service Metrics Documentation

## Overview

The `SlicerServiceMetrics` class provides comprehensive observability metrics for slicer service operations in PrintFarmer. These metrics enable monitoring of job lifecycles, performance, capacity utilization, and service health.

## Metric Categories

### 1. Job Lifecycle Metrics (Counters)

Track the flow of jobs through the slicer service system:

- **`printfarmer.slicer_service.jobs.submitted.total`**
  - Type: Counter
  - Description: Total number of slice jobs submitted to the system
  - Tags: `slicer_type` (e.g., "PrusaSlicer", "OrcaSlicer")

- **`printfarmer.slicer_service.jobs.started.total`**
  - Type: Counter
  - Description: Total number of jobs that started execution
  - Tags: `slicer_type`, `service_id`

- **`printfarmer.slicer_service.jobs.completed.total`**
  - Type: Counter
  - Description: Total number of jobs that completed successfully
  - Tags: `slicer_type`, `service_id`

- **`printfarmer.slicer_service.jobs.failed.total`**
  - Type: Counter
  - Description: Total number of jobs that failed
  - Tags: `slicer_type`, `service_id`, `failure_reason`

- **`printfarmer.slicer_service.jobs.cancelled.total`**
  - Type: Counter
  - Description: Total number of jobs that were cancelled
  - Tags: `slicer_type`, `service_id`, `cancellation_reason`

- **`printfarmer.slicer_service.jobs.failures.by_reason`**
  - Type: Counter
  - Description: Job failures categorized by specific failure reason
  - Tags: `failure_reason`

### 2. Job Duration Metrics (Histograms)

Measure job performance and latency:

- **`printfarmer.slicer_service.job.queue_duration.seconds`**
  - Type: Histogram
  - Description: Time a job spent waiting in queue before execution
  - Tags: `slicer_type`
  - Unit: seconds

- **`printfarmer.slicer_service.job.execution_duration.seconds`**
  - Type: Histogram
  - Description: Time a job spent executing (slicing)
  - Tags: `slicer_type`, `service_id`
  - Unit: seconds

- **`printfarmer.slicer_service.job.total_duration.seconds`**
  - Type: Histogram
  - Description: Total job duration from submission to completion
  - Tags: `slicer_type`, `service_id`
  - Unit: seconds

### 3. Capacity Metrics (Observable Gauges)

Real-time view of slicer service capacity:

- **`printfarmer.slicer_service.capacity.total`**
  - Type: ObservableGauge
  - Description: Total concurrent job capacity across all workers
  - Tags: `slicer_type`
  - Updates: Real-time via callback

- **`printfarmer.slicer_service.capacity.available`**
  - Type: ObservableGauge
  - Description: Currently available job slots (free capacity)
  - Tags: `slicer_type`
  - Updates: Real-time via callback

- **`printfarmer.slicer_service.jobs.active`**
  - Type: ObservableGauge
  - Description: Number of jobs currently being processed
  - Tags: `slicer_type`
  - Updates: Real-time via callback

- **`printfarmer.slicer_service.capacity.utilization`**
  - Type: Histogram
  - Description: Capacity utilization percentage (0-100)
  - Tags: `service_id`
  - Unit: percentage
  - Recorded on: heartbeats

### 4. Service Health Metrics (Counters & Histogram)

Monitor worker registration, heartbeats, and health:

- **`printfarmer.slicer_service.registrations.total`**
  - Type: Counter
  - Description: Total number of slicer service registrations
  - Tags: `slicer_type`

- **`printfarmer.slicer_service.deregistrations.total`**
  - Type: Counter
  - Description: Total number of slicer service deregistrations
  - Tags: `slicer_type`, `reason`

- **`printfarmer.slicer_service.heartbeats.total`**
  - Type: Counter
  - Description: Total number of heartbeats received
  - Tags: `service_id`

- **`printfarmer.slicer_service.heartbeats.failures.total`**
  - Type: Counter
  - Description: Total number of failed heartbeat attempts
  - Tags: `service_id`, `failure_reason`

- **`printfarmer.slicer_service.heartbeat.latency.ms`**
  - Type: Histogram
  - Description: Heartbeat processing latency
  - Tags: `service_id`
  - Unit: milliseconds

### 5. Security Metrics (Counters)

Track API key rotation events:

- **`printfarmer.slicer_service.apikey.rotations.total`**
  - Type: Counter
  - Description: Total number of API key rotations
  - Tags: `service_id`, `admin_forced` (true/false)

- **`printfarmer.slicer_service.apikey.rotation_failures.total`**
  - Type: Counter
  - Description: Total number of failed API key rotations
  - Tags: `service_id`, `failure_reason`

## Usage Examples

### Prometheus Queries

**Job Success Rate:**
```promql
rate(printfarmer_slicer_service_jobs_completed_total[5m]) / 
rate(printfarmer_slicer_service_jobs_submitted_total[5m])
```

**Job Failure Rate by Reason:**
```promql
rate(printfarmer_slicer_service_jobs_failures_by_reason[5m])
```

**Average Job Queue Time (p95):**
```promql
histogram_quantile(0.95, 
  rate(printfarmer_slicer_service_job_queue_duration_seconds_bucket[5m])
)
```

**Average Job Execution Time:**
```promql
rate(printfarmer_slicer_service_job_execution_duration_seconds_sum[5m]) /
rate(printfarmer_slicer_service_job_execution_duration_seconds_count[5m])
```

**Current Capacity Utilization:**
```promql
(printfarmer_slicer_service_capacity_total - 
 printfarmer_slicer_service_capacity_available) / 
printfarmer_slicer_service_capacity_total * 100
```

**Worker Health (Heartbeat Success Rate):**
```promql
rate(printfarmer_slicer_service_heartbeats_total[5m]) - 
rate(printfarmer_slicer_service_heartbeats_failures_total[5m])
```

**API Key Rotation Activity:**
```promql
sum by (admin_forced) (
  rate(printfarmer_slicer_service_apikey_rotations_total[1h])
)
```

### Grafana Dashboard Recommendations

**Panel 1: Job Throughput**
- Metric: `printfarmer_slicer_service_jobs_completed_total`
- Visualization: Time series graph
- Group by: `slicer_type`

**Panel 2: Job Failure Rate**
- Metric: `printfarmer_slicer_service_jobs_failed_total`
- Visualization: Time series with rate calculation
- Group by: `failure_reason`

**Panel 3: Capacity Utilization**
- Metric: `printfarmer_slicer_service_capacity_utilization`
- Visualization: Gauge
- Threshold: Warning at 80%, Critical at 95%

**Panel 4: Job Latency Distribution**
- Metrics: All duration histograms
- Visualization: Heatmap
- Show: p50, p95, p99 percentiles

**Panel 5: Service Health**
- Metrics: Heartbeat totals and failures
- Visualization: Status timeline
- Alert: On heartbeat failures

**Panel 6: Active Workers**
- Metric: `printfarmer_slicer_service_registrations_total` - `printfarmer_slicer_service_deregistrations_total`
- Visualization: Stat panel
- Group by: `slicer_type`

## Integration Details

### Dependency Injection

The metrics class is registered as a singleton in `Program.cs`:

```csharp
builder.Services.AddSingleton<Farm.Web.Api.Services.Slicing.SlicerServiceMetrics>();
```

### Service Integration

Metrics are automatically recorded by `SlicersService` at the following points:

- **RegisterAsync**: Records registration and sets up capacity providers
- **HeartbeatAsync**: Records heartbeat, latency, and capacity utilization
- **DeregisterAsync**: Records deregistration with reason
- **RotateApiKeyAsync**: Records API key rotation events

### Capacity Provider Callbacks

Observable gauges for capacity metrics use asynchronous callbacks that query the `IWorkerRepository`:

```csharp
metrics.SetCapacityProviders(
    GetTotalCapacitySync,
    GetAvailableCapacitySync,
    GetActiveJobsSync
);
```

These callbacks aggregate worker data in real-time for accurate capacity reporting.

## Metric Export

Metrics are automatically exported via:
- **Prometheus endpoint**: `/metrics`
- **OpenTelemetry exporter**: Configured in Program.cs
- **Format**: Prometheus text format

## Best Practices

1. **Alerting**: Set up alerts for:
   - Job failure rate > 5%
   - Capacity utilization > 90%
   - Heartbeat failures
   - API key rotation failures

2. **Retention**: Configure appropriate retention policies for:
   - High-frequency metrics (heartbeats): 7 days
   - Job metrics: 30 days
   - Capacity metrics: 14 days

3. **Cardinality**: Be cautious with:
   - `service_id` tags (can be high cardinality)
   - Consider aggregating by `slicer_type` for dashboards

4. **Performance**: Observable gauges are evaluated on scrape, ensure:
   - Callback queries are optimized
   - Database indexes on worker queries

## Phase 7 Task 2 Completion

This metrics implementation addresses all requirements from Phase 7 Task 2:

✅ Job duration tracking (queue, execution, total)  
✅ Failure rate monitoring with categorization  
✅ Per-service capacity tracking (total, available, active)  
✅ Real-time capacity utilization  
✅ Service health monitoring (heartbeats, latency)  
✅ Security event tracking (API key rotation)  
✅ Dimensional tagging for filtering and aggregation  
✅ Integration with existing OpenTelemetry infrastructure  
✅ Prometheus-compatible export format  

## See Also

- [OpenTelemetry Metrics Documentation](https://opentelemetry.io/docs/specs/otel/metrics/)
- [Prometheus Query Language](https://prometheus.io/docs/prometheus/latest/querying/basics/)
- [Grafana Dashboard Best Practices](https://grafana.com/docs/grafana/latest/dashboards/build-dashboards/best-practices/)
