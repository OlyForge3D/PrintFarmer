using System.Collections.Concurrent;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Bridges the scheduler's immutable, policy-bound request to the host execution engine without
/// discarding any authenticated release binding. Manual execution remains on <see cref="IHostUpdateExecutor"/>
/// and is not authorized by this adapter.
/// </summary>
public sealed class HostUpdateSchedulerExecutorAdapter(
    IHostUpdateExecutor executor,
    string hostPlatform) : IHostUpdateSchedulerExecutor, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ActiveOperation> _activeRequests = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

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

        CancellationTokenSource safeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveOperation operation = new(request.OperationToken, safeCancellation, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                safeCancellation.Dispose();
                return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "executor_disposed");
            }

            if (!_activeRequests.TryAdd(request.RequestId, operation))
            {
                safeCancellation.Dispose();
                return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "request_already_running");
            }
        }

        try
        {
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
            _activeRequests.TryRemove(new KeyValuePair<string, ActiveOperation>(request.RequestId, operation));
            safeCancellation.Dispose();
            operation.Completion.TrySetResult();
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
            await Task.WhenAll(operations.Select(operation => operation.Completion.Task)).ConfigureAwait(false);
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
