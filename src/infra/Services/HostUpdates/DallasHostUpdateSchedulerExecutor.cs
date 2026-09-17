using System.Collections.Concurrent;

namespace Farm.Infrastructure.Services.HostUpdates;

/// <summary>
/// Bridges the scheduler's immutable, policy-bound request to Dallas's execution engine without
/// discarding any authenticated release binding. Manual execution remains on <see cref="IHostUpdateExecutor"/>
/// and is not authorized by this adapter.
/// </summary>
public sealed class DallasHostUpdateSchedulerExecutor(
    IHostUpdateExecutor executor,
    string hostPlatform) : IHostUpdateSchedulerExecutor, IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);
    private int _disposed;

    public async Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "executor_disposed");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "executor_disposed");
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPlatform);

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
        if (!_activeRequests.TryAdd(request.RequestId, safeCancellation))
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, "request_already_running");
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
            _activeRequests.TryRemove(new KeyValuePair<string, CancellationTokenSource>(request.RequestId, safeCancellation));
            safeCancellation.Dispose();
        }
    }

    public async Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(requestId) && _activeRequests.TryGetValue(requestId, out CancellationTokenSource? cancellation))
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The request completed between lookup and cancellation delivery.
            }
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
