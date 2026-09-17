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
    private static readonly string[] ServiceIds = ["api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker", "monolith"];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);

    public async Task<HostUpdateExecutorResponse> ExecuteAsync(HostUpdateExecutorRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPlatform);

        HostUpdateExecutionChannel channel;
        try
        {
            channel = ParseChannel(request.Channel);
        }
        catch (ArgumentException exception)
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, exception.Message);
        }

        HostUpdateExecutionRequest executionRequest = new(
            request.ReleaseId,
            request.Sequence,
            request.ManifestDigest,
            request.SourceCommit,
            channel,
            CreateTargets(request.PlatformDigests))
        {
            RequestId = request.RequestId,
            TrustRoot = request.TrustRoot,
            PolicyRevision = request.PolicyRevision,
            PolicyFingerprint = request.PolicyFingerprint,
            HostPlatform = hostPlatform,
        };

        if (!executionRequest.IsValid(out string validationError))
        {
            return new HostUpdateExecutorResponse(HostUpdateExecutorResult.Refused, validationError);
        }

        using CancellationTokenSource safeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
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
        }
    }

    public async Task SignalSafeCheckpointCancellationAsync(string requestId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(requestId) && _activeRequests.TryGetValue(requestId, out CancellationTokenSource? cancellation))
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource cancellation in _activeRequests.Values)
        {
            cancellation.Cancel();
        }

        _activeRequests.Clear();
    }

    private static HostUpdateExecutionChannel ParseChannel(string channel) => channel switch
    {
        "stable" => HostUpdateExecutionChannel.Stable,
        "insider" => HostUpdateExecutionChannel.Insider,
        _ => throw new ArgumentException("channel_invalid", nameof(channel)),
    };

    private List<HostUpdateExecutionTarget> CreateTargets(HostUpdatePlatformDigests digests) =>
    [
        new(ServiceIds[0], hostPlatform, digests.Api),
        new(ServiceIds[1], hostPlatform, digests.Frontend),
        new(ServiceIds[2], hostPlatform, digests.SlicerHost),
        new(ServiceIds[3], hostPlatform, digests.PrinterDiscovery),
        new(ServiceIds[4], hostPlatform, digests.OrcaslicerWorker),
        new(ServiceIds[5], hostPlatform, digests.Monolith),
    ];
}
