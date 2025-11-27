using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Comprehensive metrics for slicer service operations, including job durations,
/// failure rates, and per-service capacity tracking.
/// </summary>
public sealed class SlicerServiceMetrics : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    // Job lifecycle metrics
    public Counter<long> JobsSubmittedTotal { get; }
    public Counter<long> JobsStartedTotal { get; }
    public Counter<long> JobsCompletedTotal { get; }
    public Counter<long> JobsFailedTotal { get; }
    public Counter<long> JobsCancelledTotal { get; }

    // Duration metrics
    public Histogram<double> JobQueueDurationSeconds { get; }
    public Histogram<double> JobExecutionDurationSeconds { get; }
    public Histogram<double> JobTotalDurationSeconds { get; }

    // Per-service capacity metrics
    public ObservableGauge<int> ServiceTotalCapacity { get; }
    public ObservableGauge<int> ServiceAvailableCapacity { get; }
    public ObservableGauge<int> ServiceActiveJobs { get; }
    public Histogram<int> ServiceCapacityUtilization { get; }

    // Service health metrics
    public Counter<long> ServiceRegistrations { get; }
    public Counter<long> ServiceDeregistrations { get; }
    public Counter<long> ServiceHeartbeatsTotal { get; }
    public Counter<long> ServiceHeartbeatFailuresTotal { get; }
    public Histogram<double> ServiceHeartbeatLatencyMs { get; }

    // API key rotation metrics
    public Counter<long> ApiKeyRotationsTotal { get; }
    public Counter<long> ApiKeyRotationFailuresTotal { get; }

    // Failure reason tracking
    public Counter<long> JobFailuresByReason { get; }

    private Func<int>? _getTotalCapacity;
    private Func<int>? _getAvailableCapacity;
    private Func<int>? _getActiveJobs;

    public SlicerServiceMetrics()
    {
        _meter = new Meter("PrintFarmer.SlicerService", "1.0.0");

        // Job lifecycle counters
        JobsSubmittedTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.jobs_submitted_total",
            description: "Total number of slice jobs submitted");

        JobsStartedTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.jobs_started_total",
            description: "Total number of slice jobs started by workers");

        JobsCompletedTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.jobs_completed_total",
            description: "Total number of slice jobs completed successfully");

        JobsFailedTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.jobs_failed_total",
            description: "Total number of slice jobs failed");

        JobsCancelledTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.jobs_cancelled_total",
            description: "Total number of slice jobs cancelled");

        // Duration histograms
        JobQueueDurationSeconds = _meter.CreateHistogram<double>(
            "printfarmer.slicer.job_queue_duration_seconds",
            unit: "s",
            description: "Time jobs spend in queue before execution starts");

        JobExecutionDurationSeconds = _meter.CreateHistogram<double>(
            "printfarmer.slicer.job_execution_duration_seconds",
            unit: "s",
            description: "Time jobs spend executing on workers");

        JobTotalDurationSeconds = _meter.CreateHistogram<double>(
            "printfarmer.slicer.job_total_duration_seconds",
            unit: "s",
            description: "Total time from submission to completion");

        // Capacity gauges (observable)
        ServiceTotalCapacity = _meter.CreateObservableGauge(
            "printfarmer.slicer.service_total_capacity",
            () => _getTotalCapacity?.Invoke() ?? 0,
            description: "Total job capacity across all slicer services");

        ServiceAvailableCapacity = _meter.CreateObservableGauge(
            "printfarmer.slicer.service_available_capacity",
            () => _getAvailableCapacity?.Invoke() ?? 0,
            description: "Available job capacity across all slicer services");

        ServiceActiveJobs = _meter.CreateObservableGauge(
            "printfarmer.slicer.service_active_jobs",
            () => _getActiveJobs?.Invoke() ?? 0,
            description: "Number of jobs currently executing");

        ServiceCapacityUtilization = _meter.CreateHistogram<int>(
            "printfarmer.slicer.service_capacity_utilization_percent",
            unit: "%",
            description: "Percentage of capacity in use");

        // Service health counters
        ServiceRegistrations = _meter.CreateCounter<long>(
            "printfarmer.slicer.service_registrations_total",
            description: "Total number of slicer service registrations");

        ServiceDeregistrations = _meter.CreateCounter<long>(
            "printfarmer.slicer.service_deregistrations_total",
            description: "Total number of slicer service deregistrations");

        ServiceHeartbeatsTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.service_heartbeats_total",
            description: "Total number of heartbeats received from services");

        ServiceHeartbeatFailuresTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.service_heartbeat_failures_total",
            description: "Total number of failed heartbeat attempts");

        ServiceHeartbeatLatencyMs = _meter.CreateHistogram<double>(
            "printfarmer.slicer.service_heartbeat_latency_ms",
            unit: "ms",
            description: "Heartbeat request latency");

        // API key rotation counters
        ApiKeyRotationsTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.api_key_rotations_total",
            description: "Total number of API key rotations");

        ApiKeyRotationFailuresTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.api_key_rotation_failures_total",
            description: "Total number of failed API key rotation attempts");

        // Failure tracking
        JobFailuresByReason = _meter.CreateCounter<long>(
            "printfarmer.slicer.job_failures_by_reason_total",
            description: "Job failures categorized by reason");
    }

    /// <summary>
    /// Set callbacks for observable capacity metrics.
    /// </summary>
    public void SetCapacityProviders(
        Func<int> getTotalCapacity,
        Func<int> getAvailableCapacity,
        Func<int> getActiveJobs)
    {
        _getTotalCapacity = getTotalCapacity;
        _getAvailableCapacity = getAvailableCapacity;
        _getActiveJobs = getActiveJobs;
    }

    /// <summary>
    /// Record job submission.
    /// </summary>
    public void RecordJobSubmitted(string slicerType, string? serviceId = null)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType }
        };
        if (serviceId != null)
        {
            tags.Add("service_id", serviceId);
        }
        JobsSubmittedTotal.Add(1, tags);
    }

    /// <summary>
    /// Record job start (when worker claims job).
    /// </summary>
    public void RecordJobStarted(string slicerType, string serviceId, double queueDurationSeconds)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId }
        };
        JobsStartedTotal.Add(1, tags);
        JobQueueDurationSeconds.Record(queueDurationSeconds, tags);
    }

    /// <summary>
    /// Record successful job completion with durations.
    /// </summary>
    public void RecordJobCompleted(
        string slicerType,
        string serviceId,
        double executionDurationSeconds,
        double totalDurationSeconds)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId }
        };
        JobsCompletedTotal.Add(1, tags);
        JobExecutionDurationSeconds.Record(executionDurationSeconds, tags);
        JobTotalDurationSeconds.Record(totalDurationSeconds, tags);
    }

    /// <summary>
    /// Record job failure with reason categorization.
    /// </summary>
    public void RecordJobFailed(
        string slicerType,
        string? serviceId,
        string failureReason,
        double? executionDurationSeconds = null)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "failure_reason", failureReason }
        };
        if (serviceId != null)
        {
            tags.Add("service_id", serviceId);
        }
        JobsFailedTotal.Add(1, tags);
        JobFailuresByReason.Add(1, tags);

        if (executionDurationSeconds.HasValue)
        {
            JobExecutionDurationSeconds.Record(executionDurationSeconds.Value, tags);
        }
    }

    /// <summary>
    /// Record job cancellation.
    /// </summary>
    public void RecordJobCancelled(string slicerType, string? serviceId = null)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType }
        };
        if (serviceId != null)
        {
            tags.Add("service_id", serviceId);
        }
        JobsCancelledTotal.Add(1, tags);
    }

    /// <summary>
    /// Record service registration.
    /// </summary>
    public void RecordServiceRegistration(string slicerType, string serviceId)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId }
        };
        ServiceRegistrations.Add(1, tags);
    }

    /// <summary>
    /// Record service deregistration.
    /// </summary>
    public void RecordServiceDeregistration(string slicerType, string serviceId, string reason)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
            { "reason", reason }
        };
        ServiceDeregistrations.Add(1, tags);
    }

    /// <summary>
    /// Record service heartbeat.
    /// </summary>
    public void RecordServiceHeartbeat(
        string slicerType,
        string serviceId,
        bool success,
        double latencyMs,
        int? freeSlots = null,
        int? totalSlots = null)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId }
        };

        if (success)
        {
            ServiceHeartbeatsTotal.Add(1, tags);
            ServiceHeartbeatLatencyMs.Record(latencyMs, tags);

            // Record capacity utilization if provided
            if (freeSlots.HasValue && totalSlots.HasValue && totalSlots.Value > 0)
            {
                int utilization = ((totalSlots.Value - freeSlots.Value) * 100) / totalSlots.Value;
                ServiceCapacityUtilization.Record(utilization, tags);
            }
        }
        else
        {
            ServiceHeartbeatFailuresTotal.Add(1, tags);
        }
    }

    /// <summary>
    /// Record API key rotation.
    /// </summary>
    public void RecordApiKeyRotation(string slicerType, string serviceId, bool success, bool isAdminForced = false)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
            { "admin_forced", isAdminForced.ToString().ToLowerInvariant() }
        };

        if (success)
        {
            ApiKeyRotationsTotal.Add(1, tags);
        }
        else
        {
            ApiKeyRotationFailuresTotal.Add(1, tags);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _meter.Dispose();
        _disposed = true;
    }
}
