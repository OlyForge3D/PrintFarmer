using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Services;
using CircuitState = Farm.Slicer.Module.Services.CircuitState;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Workers;

/// <summary>
/// Circuit breaker service that tracks worker failure patterns and temporarily disables
/// workers that consistently fail jobs to prevent wasted dispatch cycles.
/// </summary>
public class WorkerCircuitBreakerService(
    ILogger<WorkerCircuitBreakerService> logger,
    IOptions<CircuitBreakerSettings> settings) : IWorkerCircuitBreakerService
{
    private readonly ILogger<WorkerCircuitBreakerService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly CircuitBreakerSettings _settings = settings?.Value ?? new CircuitBreakerSettings();
    private readonly ConcurrentDictionary<Guid, WorkerCircuitState> _circuitStates = new();

    /// <summary>
    /// Record a job failure for a worker. Opens circuit if failure threshold exceeded.
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker that failed.</param>
    /// <param name="workerRepo">The worker repository for persistence operations.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task RecordJobFailureAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default)
    {
        if (workerId == Guid.Empty)
        {
            return;
        }

        WorkerCircuitState state = _circuitStates.GetOrAdd(workerId, _ => new WorkerCircuitState());

        bool shouldDisableWorker;
        int failureCount;

        lock (state.Lock)
        {
            state.RecentFailures.Add(DateTime.UtcNow);

            // Remove failures outside the window
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-_settings.WindowSeconds);
            _ = state.RecentFailures.RemoveAll(t => t < cutoff);

            failureCount = state.RecentFailures.Count;

            // Check if circuit should open
            shouldDisableWorker = failureCount >= _settings.FailureThreshold && state.State != CircuitState.Open;

            if (shouldDisableWorker)
            {
                state.State = CircuitState.Open;
                state.OpenedAt = DateTime.UtcNow;
            }
        }

        // Perform async operations outside the lock
        if (shouldDisableWorker)
        {
            _logger.LogWarning(
                "Circuit OPENED for worker {WorkerId}: {FailureCount} failures in {WindowSeconds}s",
                workerId, failureCount, _settings.WindowSeconds);

            // Disable the worker
            try
            {
                await workerRepo.DisableWorkerAsync(
                    workerId,
                    $"Circuit breaker: {failureCount} failures in {_settings.WindowSeconds}s");
                await workerRepo.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to disable worker {WorkerId} after circuit opened", workerId);
            }
        }
    }

    /// <summary>
    /// Record a job success for a worker. May close circuit if in half-open state.
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker that succeeded.</param>
    /// <param name="workerRepo">The worker repository for persistence operations.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public async Task RecordJobSuccessAsync(Guid workerId, IWorkerRepository workerRepo, CancellationToken ct = default)
    {
        if (workerId == Guid.Empty)
        {
            return;
        }

        if (!_circuitStates.TryGetValue(workerId, out WorkerCircuitState? state))
        {
            return; // No circuit state = never failed
        }

        bool shouldEnableWorker;

        lock (state.Lock)
        {
            state.RecentSuccesses++;

            shouldEnableWorker = state.State == CircuitState.HalfOpen
                && state.RecentSuccesses >= _settings.SuccessThresholdToClose;

            if (shouldEnableWorker)
            {
                state.State = CircuitState.Closed;
                state.RecentFailures.Clear();
                state.RecentSuccesses = 0;
            }
        }

        // Perform async operations outside the lock
        if (shouldEnableWorker)
        {
            _logger.LogInformation(
                "Circuit CLOSED for worker {WorkerId} after {SuccessCount} consecutive successes",
                workerId, _settings.SuccessThresholdToClose);

            // Re-enable the worker
            try
            {
                await workerRepo.EnableWorkerAsync(workerId);
                await workerRepo.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enable worker {WorkerId} after circuit closed", workerId);
            }
        }
    }

    /// <summary>
    /// Check circuits and transition open circuits to half-open after cooldown period.
    /// Should be called periodically (e.g., by a background service).
    /// </summary>
    public void CheckCircuits()
    {
        DateTime now = DateTime.UtcNow;

        foreach (KeyValuePair<Guid, WorkerCircuitState> kvp in _circuitStates)
        {
            Guid workerId = kvp.Key;
            WorkerCircuitState state = kvp.Value;

            lock (state.Lock)
            {
                if (state.State == CircuitState.Open)
                {
                    double elapsed = (now - state.OpenedAt).TotalSeconds;
                    if (elapsed >= _settings.CooldownSeconds)
                    {
                        state.State = CircuitState.HalfOpen;
                        state.RecentSuccesses = 0;
                        _logger.LogInformation(
                            "Circuit transitioned to HALF-OPEN for worker {WorkerId} after {CooldownSeconds}s cooldown",
                            workerId, _settings.CooldownSeconds);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Get current circuit state for a worker.
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker.</param>
    public CircuitState GetCircuitState(Guid workerId)
    {
        if (!_circuitStates.TryGetValue(workerId, out WorkerCircuitState? state))
        {
            return CircuitState.Closed;
        }

        lock (state.Lock)
        {
            return state.State;
        }
    }

    /// <summary>
    /// Reset circuit for a worker (for administrative override).
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker to reset.</param>
    public void ResetCircuit(Guid workerId)
    {
        if (_circuitStates.TryRemove(workerId, out WorkerCircuitState? state))
        {
            _logger.LogInformation("Circuit manually RESET for worker {WorkerId}", workerId);
        }
    }

    private sealed class WorkerCircuitState
    {
        public CircuitState State { get; set; } = CircuitState.Closed;

        public List<DateTime> RecentFailures { get; } = new();

        public int RecentSuccesses { get; set; }

        public DateTime OpenedAt { get; set; }

        public Lock Lock { get; } = new();
    }
}
