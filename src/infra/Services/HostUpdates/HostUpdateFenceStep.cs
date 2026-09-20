using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.HostUpdates;

#pragma warning disable CA1032 // These internal fault-code exceptions are only ever constructed with a code; standard constructors are not used.
/// <summary>Thrown when one or more writers could not be proven fenced within the bounded timeout.</summary>
public sealed class HostUpdateFenceProofFailedException(IReadOnlyList<string> unfencedWriterNames)
    : InvalidOperationException($"writers_not_fenced:{string.Join(',', unfencedWriterNames)}")
{
    public IReadOnlyList<string> UnfencedWriterNames { get; } = unfencedWriterNames;
}
#pragma warning restore CA1032

/// <summary>
/// One writer/background component (API replica admission, a scheduler, the outbox publisher,
/// a bridge, or a worker pool) that must stop making new writes and prove it has quiesced
/// before a coordinated backup/migration/apply may proceed.
/// </summary>
public interface IFenceableWriter
{
    string Name { get; }

    /// <summary>Requests that this writer stop starting new work. Must not cancel in-flight work.</summary>
    Task QuiesceAsync(CancellationToken cancellationToken);

    /// <summary>Reports whether this writer has actually stopped producing new writes.</summary>
    Task<bool> IsQuiescedAsync(CancellationToken cancellationToken);

    /// <summary>Resumes normal operation after a successful update, a rollback, or an abort.</summary>
    Task ResumeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fences every registered writer and proves each one quiesced, with a bounded verification
/// timeout, before allowing the executor to proceed to backup. Fails closed: any writer that
/// cannot be proven fenced blocks the update rather than proceeding on an assumption.
/// </summary>
public sealed class HostUpdateFenceCoordinator(
    IReadOnlyList<IFenceableWriter> writers,
    TimeSpan proofTimeout,
    TimeSpan pollInterval,
    TimeProvider? timeProvider = null,
    ILogger<HostUpdateFenceCoordinator>? logger = null) : IHostUpdateFenceCoordinator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<HostUpdateFenceCoordinator>? _logger = logger;

    public async Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (IFenceableWriter writer in writers)
        {
            await writer.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation(
                "host_update_writer_fence_acquired writer={WriterName} release_id={ReleaseId}",
                writer.Name,
                request.ReleaseId);
        }

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + proofTimeout;
        while (true)
        {
            List<string> unproven = await GetUnprovenWritersAsync(cancellationToken).ConfigureAwait(false);

            if (unproven.Count == 0)
            {
                return;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                _logger?.LogWarning(
                    "host_update_writer_fence_rejected release_id={ReleaseId} writers={WriterNames}",
                    request.ReleaseId,
                    string.Join(',', unproven));
                throw new HostUpdateFenceProofFailedException(unproven);
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> GetUnprovenWritersAsync(CancellationToken cancellationToken)
    {
        var unproven = new List<string>();
        foreach (IFenceableWriter writer in writers)
        {
            if (!await writer.IsQuiescedAsync(cancellationToken).ConfigureAwait(false))
            {
                unproven.Add(writer.Name);
            }
        }

        return unproven;
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        foreach (IFenceableWriter writer in writers)
        {
            await writer.ResumeAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("host_update_writer_fence_released writer={WriterName}", writer.Name);
        }
    }
}

/// <summary>Runs the fence step of the host update executor, and releases the fence on recovery.</summary>
public interface IHostUpdateFenceCoordinator
{
    Task RunAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken);

    Task ReleaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fences the HTTP admission perimeter. <see cref="IsQuiescedAsync"/> reports the gate's own
/// closed/open state; the gate genuinely blocks a submission only where a real call site
/// explicitly consults <see cref="IHostUpdateAdmissionGate.IsClosedAsync"/> before admitting new
/// work. Current consumers cover queue submission, manual dispatch, batch dispatch,
/// auto-dispatch ready acknowledgement, the auto-dispatch background loop, slicer job enqueue/claim,
/// and webhook delivery. No producer gaps remain code-owned; availability fails closed when any
/// name in <see cref="HostUpdateExecutionOptions.RequiredFencedWriterNames"/> lacks a registered
/// writer. Closing the gate without every real call site wired does not, by itself, guarantee no
/// new write starts; this class only proves the gate itself flipped, not that every producer honors
/// it.
/// </summary>
public sealed class AdmissionFenceableWriter(IHostUpdateAdmissionGate admissionGate) : IFenceableWriter
{
    public string Name => "api-admission";

    public Task QuiesceAsync(CancellationToken cancellationToken) => admissionGate.CloseAsync(cancellationToken);

    public Task<bool> IsQuiescedAsync(CancellationToken cancellationToken) => admissionGate.IsClosedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => admissionGate.OpenAsync(cancellationToken);
}

/// <summary>
/// Fences a background writer (a scheduler, the outbox publisher, a SignalR bridge, or a
/// worker pool) that reports its own quiesced state through a shared <see cref="IHostUpdateWriterActivityFlag"/>
/// consulted at the top of each of its work loop iterations.
/// </summary>
public sealed class BackgroundWriterFenceableWriter(string name, IHostUpdateWriterActivityFlag flag) : IFenceableWriter
{
    public string Name { get; } = name;

    internal IHostUpdateWriterActivityFlag ActivityFlag { get; } = flag;

    public Task QuiesceAsync(CancellationToken cancellationToken) => ActivityFlag.RequestPauseAsync(cancellationToken);

    public Task<bool> IsQuiescedAsync(CancellationToken cancellationToken) => ActivityFlag.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => ActivityFlag.ResumeAsync(cancellationToken);
}

/// <summary>
/// Shared pause/resume signal a background writer polls at the top of each loop iteration and
/// acknowledges once it has actually stopped starting new writes for that iteration.
/// </summary>
public interface IHostUpdateWriterActivityFlag
{
    Task RequestPauseAsync(CancellationToken cancellationToken);

    /// <summary>Polled by the writer's own loop to decide whether to skip starting new work this iteration.</summary>
    Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken);

    Task<bool> IsPausedAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Called by the writer's own loop once it observes the pause request and stops.</summary>
    Task AcknowledgePausedAsync(CancellationToken cancellationToken);
}

/// <summary>Process-wide, thread-safe <see cref="IHostUpdateWriterActivityFlag"/>.</summary>
public sealed class InMemoryHostUpdateWriterActivityFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private volatile bool _pauseRequested;
    private volatile bool _acknowledged;
    private int _acknowledgementCount;

    /// <summary>Lifetime acknowledgement count for tests; fence decisions do not consult it.</summary>
    internal int AcknowledgementCount => Volatile.Read(ref _acknowledgementCount);

    /// <summary>Whether the writer acknowledged the current pause epoch.</summary>
    internal bool IsAcknowledged => _acknowledged;

    public Task RequestPauseAsync(CancellationToken cancellationToken)
    {
        _acknowledged = false;
        _pauseRequested = true;
        return Task.CompletedTask;
    }

    public async Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) =>
        _pauseRequested || (durableFence is not null && await durableFence.IsClosedAsync(cancellationToken).ConfigureAwait(false));

    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken) =>
        await IsPauseRequestedAsync(cancellationToken).ConfigureAwait(false) && _acknowledged;

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        _pauseRequested = false;
        _acknowledged = false;
        if (durableFence is not null)
        {
            await durableFence.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AcknowledgePausedAsync(CancellationToken cancellationToken)
    {
        if (await IsPauseRequestedAsync(cancellationToken).ConfigureAwait(false))
        {
            _acknowledged = true;
            _ = Interlocked.Increment(ref _acknowledgementCount);
        }
    }
}

/// <summary>
/// Dedicated <see cref="IHostUpdateWriterActivityFlag"/> instance for
/// <see cref="Electricity.PowerReadingPruneService"/>. A distinct concrete type (rather than
/// another registration of the bare interface) lets dependency injection route this specific
/// writer's optional constructor parameter to its own independent pause/acknowledge state,
/// separate from every other fenced background writer.
/// </summary>
public sealed class PowerReadingPruneFenceFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;
}

/// <summary>
/// Dedicated <see cref="IHostUpdateWriterActivityFlag"/> instance for
/// <see cref="Queue.QueueRetentionPruneService"/>. See <see cref="PowerReadingPruneFenceFlag"/>
/// for why a distinct concrete type is required instead of another bare-interface registration.
/// </summary>
public sealed class QueueRetentionPruneFenceFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;
}

/// <summary>Dedicated fence flag for the durable backend-start command consumer.</summary>
public sealed class BackendStartCommandConsumerFenceFlag(IHostUpdateAdmissionGate? durableFence = null)
    : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;
}

/// <summary>Dedicated fence flag for the durable backend-control command consumer.</summary>
public sealed class BackendControlCommandConsumerFenceFlag(IHostUpdateAdmissionGate? durableFence = null)
    : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;
}

/// <summary>Dedicated fence flag for the bed-clear acknowledgement expiry scanner.</summary>
public sealed class BedClearAcknowledgementExpiryFenceFlag(IHostUpdateAdmissionGate? durableFence = null)
    : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;
}

/// <summary>
/// Dedicated <see cref="IHostUpdateWriterActivityFlag"/> instance for
/// <see cref="Queue.Dispatch.AutoDispatchBackgroundService"/> -- the auto-dispatch loop that
/// physically starts new printer dispatch workers. Named separately from the generic
/// <c>queue-outbox-publisher</c> flag (Kane/panel finding: "physical admission barrier is not
/// real" -- auto-dispatch specifically must stop *starting new dispatch workers* while fenced,
/// not merely stop publishing outbox events) so the fence coordinator can prove this producer,
/// specifically, has quiesced. See <see cref="PowerReadingPruneFenceFlag"/> for why a distinct
/// concrete type is required instead of another bare-interface registration.
/// </summary>
public sealed class AutoDispatchFenceFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);
}

/// <summary>
/// Dedicated <see cref="IHostUpdateWriterActivityFlag"/> instance for outbound webhook delivery.
/// This fences the webhook bridge so no external HTTP delivery or webhook delivery-log write can
/// start while the host-update executor is in its pre-backup/migration/apply critical section.
/// </summary>
public sealed class WebhookDeliveryFenceFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);
}

/// <summary>
/// Durable-backed fence for queue reconciliation. The concrete type keeps this writer's
/// acknowledgement state independent from other background writers while the shared admission
/// marker makes a closed fence visible to a process that starts after the update began.
/// </summary>
public sealed class QueueReconciliationFenceFlag(IHostUpdateAdmissionGate? durableFence = null) : IHostUpdateWriterActivityFlag
{
    private readonly InMemoryHostUpdateWriterActivityFlag _inner = new(durableFence);

    public Task RequestPauseAsync(CancellationToken cancellationToken) => _inner.RequestPauseAsync(cancellationToken);

    public Task<bool> IsPauseRequestedAsync(CancellationToken cancellationToken) => _inner.IsPauseRequestedAsync(cancellationToken);

    public Task<bool> IsPausedAsync(CancellationToken cancellationToken) => _inner.IsPausedAsync(cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => _inner.ResumeAsync(cancellationToken);

    public Task AcknowledgePausedAsync(CancellationToken cancellationToken) => _inner.AcknowledgePausedAsync(cancellationToken);

    internal int AcknowledgementCount => _inner.AcknowledgementCount;

    internal bool IsAcknowledged => _inner.IsAcknowledged;
}
