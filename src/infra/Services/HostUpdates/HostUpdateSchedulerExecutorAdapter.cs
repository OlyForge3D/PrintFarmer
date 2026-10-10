using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Bridges the scheduler's immutable, policy-bound request to the host execution engine without
/// discarding any authenticated release binding. Manual execution remains on <see cref="IHostUpdateExecutor"/>
/// and is not authorized by this adapter.
/// </summary>
public sealed class HostUpdateSchedulerExecutorAdapter(
    IHostUpdateExecutor executor,
    string hostPlatform,
    ILogger<HostUpdateSchedulerExecutorAdapter>? logger = null) : IHostUpdateSchedulerExecutor, IDisposable, IAsyncDisposable
{
    private const int MaxPreArmedRequests = 16;
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<HostUpdateSchedulerExecutorAdapter> _logger = logger ?? NullLogger<HostUpdateSchedulerExecutorAdapter>.Instance;
    private readonly ConcurrentDictionary<string, ActiveOperation> _activeRequests = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // This is deliberately capped because the adapter is scoped per scheduler tick; a future
    // lifetime change must not turn cancellation requests into unbounded retained state.
    private readonly ConcurrentDictionary<string, string> _preArmed = new(StringComparer.Ordinal);
    private bool _preArmOverflowed;
    private int _disposed;

    // Internal fault-injection seam after insertion, while the lifecycle lock is held.
    internal Action<CancellationTokenSource, Task>? OnOperationRegisteredForTests { get; set; }

    public async Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPlatform);

        if (string.IsNullOrWhiteSpace(request.OperationToken))
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "operation_token_missing");
        }

        HostUpdateExecutionRequest executionRequest;
        try
        {
            executionRequest = HostUpdateExecutionRequestBuilder.FromExecutorRequest(
                request,
                hostPlatform,
                HostUpdateAuthorizationKind.StandingPolicy);
        }
        catch (ArgumentException exception)
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, exception.Message);
        }

        if (!executionRequest.IsValid(out string validationError))
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, validationError);
        }

        ActiveOperation? operation = null;
        CancellationTokenSource safeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            operation = new(request.OperationToken, safeCancellation, new(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "executor_disposed");
                }

                if (_preArmOverflowed)
                {
                    return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "prearmed_cancellation_capacity_exceeded");
                }

                if (!_activeRequests.TryAdd(request.RequestId, operation))
                {
                    return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "request_already_running");
                }

                OnOperationRegisteredForTests?.Invoke(safeCancellation, operation.Completion.Task);

                if (_preArmed.TryRemove(request.RequestId, out string? token) &&
                    string.Equals(token, request.OperationToken, StringComparison.Ordinal))
                {
#pragma warning disable CA1849 // Pre-arm runs under the lifecycle lock and must synchronously publish cancellation before execution starts.
                    safeCancellation.Cancel();
#pragma warning restore CA1849
                }
            }

            HostUpdateExecutionResult result;
            try
            {
                result = await executor.ExecuteAsync(executionRequest, safeCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (safeCancellation.IsCancellationRequested)
            {
                return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "canceled_at_safe_checkpoint");
            }

            if (result.State == HostUpdateExecutionState.Completed)
            {
                return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Accepted);
            }

            if (result.State == HostUpdateExecutionState.RecoveryRequired)
            {
                return new HostUpdateExecutorResponse(HostUpdateExecutorResult.RecoveryRequired, result.FailureCode ?? "recovery_required");
            }

            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, result.FailureCode ?? "canceled_at_safe_checkpoint");
        }
        finally
        {
            if (operation is not null)
            {
                // A refused duplicate must not remove the already-running generation.
                _activeRequests.TryRemove(new KeyValuePair<string, ActiveOperation>(request.RequestId, operation));
            }

            safeCancellation.Dispose();
            operation?.Completion.TrySetResult();
        }
    }

    /// <summary>
    /// Delivers cancellation only to the exact generation the signal was captured for. A signal
    /// held across the end of one run must not cancel a later run that reuses the same request ID,
    /// so the operation token is compared as well as the request ID.
    /// </summary>
    public async Task SignalSafeCheckpointCancellationAsync(HostUpdateCancellationSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(signal.RequestId) || string.IsNullOrWhiteSpace(signal.OperationToken) ||
            !_activeRequests.TryGetValue(signal.RequestId, out ActiveOperation? operation) ||
            !string.Equals(operation.OperationToken, signal.OperationToken, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await operation.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The request completed between lookup and cancellation delivery.
        }
    }

    public void PreArmCancellation(HostUpdateCancellationSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (string.IsNullOrWhiteSpace(signal.RequestId) || string.IsNullOrWhiteSpace(signal.OperationToken))
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (_activeRequests.TryGetValue(signal.RequestId, out ActiveOperation? operation) &&
                string.Equals(operation.OperationToken, signal.OperationToken, StringComparison.Ordinal))
            {
                operation.Cancellation.Cancel();
                return;
            }

            if (_preArmed.Count >= MaxPreArmedRequests && !_preArmed.ContainsKey(signal.RequestId))
            {
                _preArmOverflowed = true;
                _logger.LogWarning("host_update_prearmed_cancellation_capacity_reached");
                return;
            }

            _preArmed[signal.RequestId] = signal.OperationToken;
        }
    }

#pragma warning disable VSTHRD002
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

    public async ValueTask DisposeAsync()
    {
        ActiveOperation[]? operations = null;
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                operations = [.. _activeRequests.Values];
            }
        }

        if (operations is null)
        {
#pragma warning disable VSTHRD003
            await _disposeCompletion.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            return;
        }

        List<Exception> cancellationErrors = [];
        foreach (ActiveOperation operation in operations)
        {
            try
            {
                await operation.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                cancellationErrors.Add(exception);
            }
        }

        try
        {
#pragma warning disable VSTHRD003
            Task drain = Task.WhenAll(operations.Select(operation => operation.Completion.Task));
            if (await Task.WhenAny(drain, Task.Delay(DisposeDrainTimeout)).ConfigureAwait(false) != drain)
            {
                _logger.LogWarning("host_update_executor_shutdown_drain_timed_out");
            }
#pragma warning restore VSTHRD003

            if (cancellationErrors.Count > 0)
            {
                throw new AggregateException("host_update_shutdown_cancellation_failed", cancellationErrors);
            }
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
        finally
        {
            _disposeCompletion.TrySetResult();
        }
    }

    private sealed record ActiveOperation(
        string OperationToken,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Completion);
}
