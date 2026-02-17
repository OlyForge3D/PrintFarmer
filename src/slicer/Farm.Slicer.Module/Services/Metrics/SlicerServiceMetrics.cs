using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Farm.Slicer.Module.Services.Metrics;

/// <summary>
/// Comprehensive metrics for slicer service operations, including job durations,
/// failure rates, and per-service capacity tracking.
/// </summary>
public sealed class SlicerServiceMetrics : IDisposable
{
    private readonly Meter _meter;
    private bool _disposed;

    private Func<int>? _getTotalCapacity;
    private Func<int>? _getAvailableCapacity;
    private Func<int>? _getActiveJobs;

    /// <summary>Gets the counter for total submitted jobs.</summary>
    public Counter<long> JobsSubmittedTotal { get; }

    /// <summary>Gets the counter for total started jobs.</summary>
    public Counter<long> JobsStartedTotal { get; }

    /// <summary>Gets the counter for total completed jobs.</summary>
    public Counter<long> JobsCompletedTotal { get; }

    /// <summary>Gets the counter for total failed jobs.</summary>
    public Counter<long> JobsFailedTotal { get; }

    /// <summary>Gets the counter for total cancelled jobs.</summary>
    public Counter<long> JobsCancelledTotal { get; }

    /// <summary>Gets the histogram for queue duration.</summary>
    public Histogram<double> JobQueueDurationSeconds { get; }

    /// <summary>Gets the histogram for execution duration.</summary>
    public Histogram<double> JobExecutionDurationSeconds { get; }

    /// <summary>Gets the histogram for total job duration.</summary>
    public Histogram<double> JobTotalDurationSeconds { get; }

    /// <summary>Gets the observable gauge for total service capacity.</summary>
    public ObservableGauge<int> ServiceTotalCapacity { get; }

    /// <summary>Gets the observable gauge for available service capacity.</summary>
    public ObservableGauge<int> ServiceAvailableCapacity { get; }

    /// <summary>Gets the observable gauge for active jobs.</summary>
    public ObservableGauge<int> ServiceActiveJobs { get; }

    /// <summary>Gets the histogram for capacity utilization percentage.</summary>
    public Histogram<int> ServiceCapacityUtilization { get; }

    /// <summary>Gets the counter for service registrations.</summary>
    public Counter<long> ServiceRegistrations { get; }

    /// <summary>Gets the counter for service deregistrations.</summary>
    public Counter<long> ServiceDeregistrations { get; }

    /// <summary>Gets the counter for total heartbeats.</summary>
    public Counter<long> ServiceHeartbeatsTotal { get; }

    /// <summary>Gets the counter for failed heartbeats.</summary>
    public Counter<long> ServiceHeartbeatFailuresTotal { get; }

    /// <summary>Gets the histogram for heartbeat latency.</summary>
    public Histogram<double> ServiceHeartbeatLatencyMs { get; }

    /// <summary>Gets the counter for API key rotations.</summary>
    public Counter<long> ApiKeyRotationsTotal { get; }

    /// <summary>Gets the counter for failed API key rotations.</summary>
    public Counter<long> ApiKeyRotationFailuresTotal { get; }

    /// <summary>Gets the counter for job failures by reason.</summary>
    public Counter<long> JobFailuresByReason { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SlicerServiceMetrics"/> class.
    /// </summary>
    public SlicerServiceMetrics()
    {
        _meter = new Meter("PrintFarmer.SlicerService", "1.0.0");

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

        ApiKeyRotationsTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.api_key_rotations_total",
            description: "Total number of API key rotations");
        ApiKeyRotationFailuresTotal = _meter.CreateCounter<long>(
            "printfarmer.slicer.api_key_rotation_failures_total",
            description: "Total number of failed API key rotation attempts");

        JobFailuresByReason = _meter.CreateCounter<long>(
            "printfarmer.slicer.job_failures_by_reason_total",
            description: "Job failures categorized by reason");
    }

    /// <summary>
    /// Set callbacks for observable capacity metrics.
    /// </summary>
    /// <param name="getTotalCapacity">Callback to retrieve total job capacity.</param>
    /// <param name="getAvailableCapacity">Callback to retrieve available job capacity.</param>
    /// <param name="getActiveJobs">Callback to retrieve the number of active jobs.</param>
    public void SetCapacityProviders(
        Func<int> getTotalCapacity,
        Func<int> getAvailableCapacity,
        Func<int> getActiveJobs)
    {
        _getTotalCapacity = getTotalCapacity;
        _getAvailableCapacity = getAvailableCapacity;
        _getActiveJobs = getActiveJobs;
    }

    /// <summary>Record job submission.</summary>
    /// <param name="slicerType">The type of slicer being used.</param>
    /// <param name="serviceId">Optional slicer service identifier.</param>
    public void RecordJobSubmitted(string slicerType, string? serviceId = null)
    {
        TagList tags = new TagList { { "slicer_type", slicerType } };
        if (serviceId != null)
        {
            tags.Add("service_id", serviceId);
        }

        JobsSubmittedTotal.Add(1, tags);
    }

    /// <summary>Record job start (when worker claims job).</summary>
    /// <param name="slicerType">The type of slicer being used.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    /// <param name="queueDurationSeconds">The time the job spent in queue, in seconds.</param>
    public void RecordJobStarted(string slicerType, string serviceId, double queueDurationSeconds)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
        };
        JobsStartedTotal.Add(1, tags);
        JobQueueDurationSeconds.Record(queueDurationSeconds, tags);
    }

    /// <summary>Record successful job completion with durations.</summary>
    /// <param name="slicerType">The type of slicer being used.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    /// <param name="executionDurationSeconds">Execution time in seconds.</param>
    /// <param name="totalDurationSeconds">Total time from submission to completion in seconds.</param>
    public void RecordJobCompleted(
        string slicerType,
        string serviceId,
        double executionDurationSeconds,
        double totalDurationSeconds)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
        };
        JobsCompletedTotal.Add(1, tags);
        JobExecutionDurationSeconds.Record(executionDurationSeconds, tags);
        JobTotalDurationSeconds.Record(totalDurationSeconds, tags);
    }

    /// <summary>Record job failure with reason categorization.</summary>
    /// <param name="slicerType">The type of slicer being used.</param>
    /// <param name="serviceId">Optional slicer service identifier.</param>
    /// <param name="failureReason">The reason for the job failure.</param>
    /// <param name="executionDurationSeconds">Optional execution duration before failure.</param>
    public void RecordJobFailed(
        string slicerType,
        string? serviceId,
        string failureReason,
        double? executionDurationSeconds = null)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "failure_reason", failureReason },
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

    /// <summary>Record job cancellation.</summary>
    /// <param name="slicerType">The type of slicer being used.</param>
    /// <param name="serviceId">Optional slicer service identifier.</param>
    public void RecordJobCancelled(string slicerType, string? serviceId = null)
    {
        TagList tags = new TagList { { "slicer_type", slicerType } };
        if (serviceId != null)
        {
            tags.Add("service_id", serviceId);
        }

        JobsCancelledTotal.Add(1, tags);
    }

    /// <summary>Record service registration.</summary>
    /// <param name="slicerType">The type of slicer being registered.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    public void RecordServiceRegistration(string slicerType, string serviceId)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
        };
        ServiceRegistrations.Add(1, tags);
    }

    /// <summary>Record service deregistration.</summary>
    /// <param name="slicerType">The type of slicer being deregistered.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    /// <param name="reason">The reason for deregistration.</param>
    public void RecordServiceDeregistration(string slicerType, string serviceId, string reason)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
            { "reason", reason },
        };
        ServiceDeregistrations.Add(1, tags);
    }

    /// <summary>Record service heartbeat.</summary>
    /// <param name="slicerType">The type of slicer sending the heartbeat.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    /// <param name="success">Whether the heartbeat was successful.</param>
    /// <param name="latencyMs">The heartbeat latency in milliseconds.</param>
    /// <param name="freeSlots">Optional number of free job slots.</param>
    /// <param name="totalSlots">Optional total number of job slots.</param>
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
            { "service_id", serviceId },
        };

        if (success)
        {
            ServiceHeartbeatsTotal.Add(1, tags);
            ServiceHeartbeatLatencyMs.Record(latencyMs, tags);

            if (freeSlots.HasValue && totalSlots.HasValue && totalSlots.Value > 0)
            {
                int utilization = (totalSlots.Value - freeSlots.Value) * 100 / totalSlots.Value;
                ServiceCapacityUtilization.Record(utilization, tags);
            }
        }
        else
        {
            ServiceHeartbeatFailuresTotal.Add(1, tags);
        }
    }

    /// <summary>Record API key rotation.</summary>
    /// <param name="slicerType">The type of slicer whose API key is being rotated.</param>
    /// <param name="serviceId">The slicer service identifier.</param>
    /// <param name="success">Whether the rotation was successful.</param>
    /// <param name="isAdminForced">Whether the rotation was forced by an administrator.</param>
    public void RecordApiKeyRotation(string slicerType, string serviceId, bool success, bool isAdminForced = false)
    {
        TagList tags = new TagList
        {
            { "slicer_type", slicerType },
            { "service_id", serviceId },
            { "admin_forced", isAdminForced.ToString().ToLowerInvariant() },
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

    /// <inheritdoc/>
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
