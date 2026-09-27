using System.Collections.Concurrent;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.HostUpdate.Cli;

/// <summary>
/// Authenticated prior-release evidence for one offline activation (issue #3126). An offline
/// bundle carries the signed prior release it upgrades from; once that prior manifest is
/// re-verified against the operator's trusted root, a writer still running the exact installed
/// prior release is a known N-1 writer rather than an unknown one. Installed state alone never
/// vouches for a running writer: the prior set must be authenticated here, the installed state
/// must bind to it exactly, and every running writer container must run the installed pin. Even
/// then the writer is only tolerated, never trusted: <see cref="HostUpdatePriorReleaseWriterFence"/>
/// must prove it fenced and drained before backup, migration or apply.
/// </summary>
internal sealed class HostUpdatePriorReleaseContext
{
    private Authenticated? _prior;
    private ConcurrentDictionary<string, string[]> _tolerated = new(StringComparer.Ordinal);

    /// <summary>The authenticated staged target and prior set, or null when none was proven.</summary>
    internal Authenticated? Prior => Volatile.Read(ref _prior);

    /// <summary>Running writer containers the absence probe tolerated, by service id, with their exact images.</summary>
    internal IReadOnlyDictionary<string, string[]> ToleratedWriters => _tolerated;

    internal void Authenticate(HostUpdateOfflineAdmission.StagedRelease staged, HostUpdateOfflineRecovery.PriorSet prior) =>
        Volatile.Write(ref _prior, new Authenticated(
            staged ?? throw new ArgumentNullException(nameof(staged)),
            prior ?? throw new ArgumentNullException(nameof(prior))));

    internal void RecordTolerated(IReadOnlyDictionary<string, string[]> tolerated) =>
        Volatile.Write(ref _tolerated, new ConcurrentDictionary<string, string[]>(tolerated, StringComparer.Ordinal));

    /// <summary>
    /// Re-verifies the staged bundle's prior set offline and records it only when its signature
    /// verifies against the same trusted root as the target. Any failure leaves no prior, so a
    /// running writer stays refused exactly as before.
    /// </summary>
    internal static async Task AuthenticateAsync(
        HostUpdatePriorReleaseContext context,
        HostUpdateCliArguments args,
        HostUpdateOfflineAdmission.StagedRelease staged,
        CancellationToken cancellationToken)
    {
        if (!HostUpdateOfflineRecovery.TryReadPrior(staged, out HostUpdateOfflineRecovery.PriorSet? prior, out _))
        {
            return;
        }

        var priorStaged = staged with
        {
            Candidate = staged.Candidate with { Channel = prior!.Manifest.Channel },
            ManifestBytes = prior.ManifestBytes,
            SignatureBytes = prior.SignatureBytes,
        };
        if (await HostUpdateOfflineAdmission.VerifySignatureAsync(args, priorStaged, cancellationToken).ConfigureAwait(false) is null)
        {
            context.Authenticate(staged, prior);
        }
    }

    internal sealed record Authenticated(HostUpdateOfflineAdmission.StagedRelease Staged, HostUpdateOfflineRecovery.PriorSet Prior);
}

/// <summary>
/// Fences a tolerated N-1 writer before the executor's backup, migration and apply (issue
/// #3126). The running prior release honours the durable admission fence, so quiescence is
/// proven from outside the process: the admission gate is closed, every active-work port reads
/// zero, and a fresh compose observation shows no writer container other than the exact ones
/// the absence probe tolerated. Anything unproven -- a read failure, a new or re-imaged writer --
/// keeps this writer unfenced, so the fence step times out instead of proceeding. When the
/// absence probe tolerated no running writer there is nothing to fence and this writer is inert.
/// </summary>
internal sealed class HostUpdatePriorReleaseWriterFence(
    HostUpdatePriorReleaseContext priorContext,
    IHostUpdateAdmissionGate admissionGate,
    IServiceScopeFactory scopeFactory,
    IHostUpdateOfflineActivationSafetyProbe safetyProbe) : IFenceableWriter
{
    public const string WriterName = "prior-release-writer";

    public string Name => WriterName;

    public Task QuiesceAsync(CancellationToken cancellationToken) =>
        priorContext.ToleratedWriters.Count == 0 ? Task.CompletedTask : admissionGate.CloseAsync(cancellationToken);

    public async Task<bool> IsQuiescedAsync(CancellationToken cancellationToken)
    {
        if (priorContext.ToleratedWriters.Count == 0)
        {
            return true;
        }

        try
        {
            if (!await admissionGate.IsClosedAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            using (IServiceScope scope = scopeFactory.CreateScope())
            {
                foreach (IActiveWorkObservationPort port in scope.ServiceProvider.GetServices<IActiveWorkObservationPort>())
                {
                    if (await port.CountActiveAsync(cancellationToken).ConfigureAwait(false) != 0)
                    {
                        return false;
                    }
                }
            }

            return safetyProbe is DockerComposeApiAbsenceProbe probe
                && await probe.RecheckToleratedWritersAsync(cancellationToken).ConfigureAwait(false) is null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    // Admission is reopened by the admission writer; the prior release is replaced by apply.
    public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
