using System.Data.Common;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.Gcode;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>Per-hop health of the durable calibration generation path.</summary>
/// <remarks>
/// Every flag answers one production hop. A flag is only true when the hop was actually resolved and
/// answered a probe in this process; a registered type, a configuration switch or a test double is
/// never accepted as evidence.
/// </remarks>
public sealed record CalibrationGenerationCapabilityDto
{
    /// <summary>Whether an attempt can currently be generated end to end.</summary>
    public required bool Operational { get; init; }

    /// <summary>Whether the deterministic generation core is registered in this process.</summary>
    public required bool DeterministicCoreAvailable { get; init; }

    /// <summary>Whether stored model bytes are resolvable through authorized storage.</summary>
    public required bool ModelStorageRoutable { get; init; }

    /// <summary>Whether the canonical slice submission path answers from this process.</summary>
    public required bool SliceSubmissionRoutable { get; init; }

    /// <summary>Whether slicer artifacts are readable and writable from this process.</summary>
    public required bool ArtifactSourceRoutable { get; init; }

    /// <summary>Whether a registered worker attests an allow-listed upstream slicer identity.</summary>
    public required bool PinnedWorkerAvailable { get; init; }

    /// <summary>Whether the artifact promotion hop reports itself operational.</summary>
    public required bool PromotionOperational { get; init; }

    /// <summary>Whether the durable orchestration store answers queries.</summary>
    public required bool OrchestrationStoreAvailable { get; init; }

    /// <summary>Whether the recovery service is wired and has not given up.</summary>
    public required bool RecoveryHealthy { get; init; }

    /// <summary>Stable machine-readable reason when generation is unavailable.</summary>
    public string? UnavailableCode { get; init; }

    /// <summary>Fresh upstream OrcaSlicer versions observed in the worker registry.</summary>
    public IReadOnlyList<string> ObservedWorkerVersions { get; init; } = [];

    /// <summary>Configured bounded allow-list used for compatibility.</summary>
    public IReadOnlyList<string> SupportedSlicerVersions { get; init; } = [];
}

/// <summary>
/// Probes every production hop the durable calibration generation saga depends on.
/// </summary>
public interface ICalibrationGenerationCapabilityProbe
{
    /// <summary>Evaluates the current per-hop capability.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The per-hop capability snapshot.</returns>
    Task<CalibrationGenerationCapabilityDto> GetCapabilityAsync(CancellationToken cancellationToken);

    /// <summary>Finds a registered worker that attests an allow-listed upstream slicer identity.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attested identity, or <see langword="null"/> when none is eligible.</returns>
    Task<CalibrationPinnedSlicerIdentity?> FindPinnedWorkerAsync(CancellationToken cancellationToken);
}

/// <summary>Shared health of the calibration generation recovery service.</summary>
/// <remarks>
/// Registration proves the recovery loop is wired; consecutive failures prove it cannot currently
/// resume interrupted runs, which must make the capability false rather than optimistic.
/// </remarks>
public sealed class CalibrationGenerationRecoveryState
{
    private const int UnhealthyFailureThreshold = 3;

    private int _consecutiveFailures;

    /// <summary>Gets whether the recovery loop can currently resume interrupted runs.</summary>
    public bool IsHealthy => Volatile.Read(ref _consecutiveFailures) < UnhealthyFailureThreshold;

    /// <summary>Gets the UTC timestamp of the last completed recovery pass.</summary>
    public DateTime? LastRunAtUtc { get; private set; }

    /// <summary>Records a successful recovery pass.</summary>
    /// <param name="completedAtUtc">The UTC completion timestamp.</param>
    public void RecordSuccess(DateTime completedAtUtc)
    {
        LastRunAtUtc = completedAtUtc;
        _ = Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    /// <summary>Records a failed recovery pass.</summary>
    public void RecordFailure() => _ = Interlocked.Increment(ref _consecutiveFailures);
}

/// <summary>
/// Default <see cref="ICalibrationGenerationCapabilityProbe"/>.
/// </summary>
/// <remarks>
/// A split deployment does not load the slicer module in this process, so the model, artifact and
/// promotion hops resolve to nothing and generation stays false with an explicit reason. Presence alone
/// is never sufficient in either topology: each hop must answer a real query before it counts.
/// </remarks>
public sealed class CalibrationGenerationCapabilityProbe(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    CalibrationGenerationRecoveryState recoveryState,
    ILogger<CalibrationGenerationCapabilityProbe> logger,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ICalibrationGenerationCapabilityProbe
{
    private static readonly TimeSpan WorkerHeartbeatFreshness = TimeSpan.FromMinutes(2);

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    private readonly IServiceProvider _serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    private readonly CalibrationGenerationRecoveryState _recoveryState =
        recoveryState ?? throw new ArgumentNullException(nameof(recoveryState));

    private readonly ILogger<CalibrationGenerationCapabilityProbe> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <inheritdoc/>
    public async Task<CalibrationGenerationCapabilityDto> GetCapabilityAsync(
        CancellationToken cancellationToken)
    {
        bool deterministicCore = HasDeterministicCore();
        bool modelStorage = await IsModelStorageRoutableAsync(cancellationToken);
        bool sliceSubmission = await IsSliceSubmissionRoutableAsync(cancellationToken);
        bool artifactSource = await IsArtifactSourceRoutableAsync(cancellationToken);
        WorkerCompatibilitySnapshot workerCompatibility =
            await FindWorkerCompatibilityAsync(cancellationToken);
        CalibrationPinnedSlicerIdentity? pinned = workerCompatibility.PinnedIdentity;
        bool promotion = await IsPromotionOperationalAsync(cancellationToken);
        bool orchestrationStore = await IsOrchestrationStoreAvailableAsync(cancellationToken);
        bool recoveryHealthy = _recoveryState.IsHealthy;

        bool operational =
            deterministicCore &&
            modelStorage &&
            sliceSubmission &&
            artifactSource &&
            pinned is not null &&
            promotion &&
            orchestrationStore &&
            recoveryHealthy;

        string? unavailableCode = operational
            ? null
            : !deterministicCore
                ? "generation_core_unavailable"
                : !modelStorage
                    ? CalibrationGenerationProblemCodes.ModelStorageUnavailable
                    : !sliceSubmission
                        ? CalibrationGenerationProblemCodes.SliceSubmissionUnavailable
                        : !artifactSource
                            ? "artifact_source_unroutable"
                            : pinned is null
                                ? workerCompatibility.HasObservedVersions &&
                                  !workerCompatibility.HasSupportedVersion
                                    ? CalibrationGenerationProblemCodes.SlicerVersionUnsupported
                                    : CalibrationGenerationProblemCodes.PinnedWorkerUnavailable
                                : !promotion
                                    ? CalibrationGenerationProblemCodes.PromotionUnavailable
                                    : !orchestrationStore
                                        ? "orchestration_store_unavailable"
                                        : "generation_recovery_unavailable";

        if (!operational && IsSplitDeployment())
        {
            unavailableCode = "split_routing_unavailable";
        }

        return new CalibrationGenerationCapabilityDto
        {
            Operational = operational,
            DeterministicCoreAvailable = deterministicCore,
            ModelStorageRoutable = modelStorage,
            SliceSubmissionRoutable = sliceSubmission,
            ArtifactSourceRoutable = artifactSource,
            PinnedWorkerAvailable = pinned is not null,
            PromotionOperational = promotion,
            OrchestrationStoreAvailable = orchestrationStore,
            RecoveryHealthy = recoveryHealthy,
            UnavailableCode = unavailableCode,
            ObservedWorkerVersions = workerCompatibility.ObservedVersions,
            SupportedSlicerVersions = _compatibilityPolicy.SupportedVersions,
        };
    }

    /// <inheritdoc/>
    public async Task<CalibrationPinnedSlicerIdentity?> FindPinnedWorkerAsync(
        CancellationToken cancellationToken) =>
        (await FindWorkerCompatibilityAsync(cancellationToken)).PinnedIdentity;

    private async Task<WorkerCompatibilitySnapshot> FindWorkerCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Slicer:Enabled", false))
        {
            return WorkerCompatibilitySnapshot.Empty;
        }

        IDbContextFactory<SlicerDbContext>? factory =
            _serviceProvider.GetService<IDbContextFactory<SlicerDbContext>>();
        if (factory is null)
        {
            return WorkerCompatibilitySnapshot.Empty;
        }

        try
        {
            await using SlicerDbContext db = await factory.CreateDbContextAsync(cancellationToken);
            DateTime heartbeatCutoff = DateTime.UtcNow - WorkerHeartbeatFreshness;
            List<ServiceAttestation> services = await db.SlicerServices
                .AsNoTracking()
                .Where(service =>
                    service.SlicerType == (int)SlicerType.OrcaSlicer &&
                    service.Status == WorkerStatus.Online &&
                    service.LastSeen >= heartbeatCutoff)
                .Select(service => new ServiceAttestation(
                    service.Id,
                    service.Version,
                    service.CapabilitiesJson))
                .ToListAsync(cancellationToken);

            string[] observedVersions = services
                .Where(service =>
                    CalibrationContractConstants.AttestsUpstreamSlicer(service.CapabilitiesJson))
                .Select(service => service.Version)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            bool hasSupportedVersion =
                observedVersions.Any(_compatibilityPolicy.IsSupported);

            string[] eligibleServiceIds = services
                .Where(service =>
                    _compatibilityPolicy.IsSupported(service.Version) &&
                    CalibrationContractConstants.AttestsUpstreamSlicer(service.CapabilitiesJson) &&
                    CalibrationSlicerAttestation.TryRead(service.CapabilitiesJson, out _, out _))
                .Select(service => service.Id.ToString())
                .ToArray();
            if (eligibleServiceIds.Length == 0)
            {
                return new(null, observedVersions, hasSupportedVersion);
            }

            // Identity/attestation selection answers "is there a worker in good standing that attests
            // an allow-listed upstream slicer identity", not "is there free capacity right now". A worker
            // claiming and actively running the very job this probe is being asked about is healthy,
            // online, authenticated and correctly attested; it must stay pinned-available while busy.
            // Whether a *new* attempt can be scheduled onto it is a queue/claim concern handled where
            // slice jobs are submitted and dispatched, not here.
            List<WorkerAttestation> workers = await db.Workers
                .AsNoTracking()
                .Where(worker =>
                    eligibleServiceIds.Contains(worker.ServiceId) &&
                    !worker.IsDisabled &&
                    worker.Status == WorkerStatus.Online &&
                    worker.LastHeartbeat >= heartbeatCutoff &&
                    worker.ApiKey != null &&
                    worker.ApiKey != string.Empty)
                .Select(worker => new WorkerAttestation(
                    worker.Id,
                    worker.ServiceId,
                    worker.CapabilitiesJson))
                .ToListAsync(cancellationToken);

            foreach (WorkerAttestation worker in workers.OrderBy(worker => worker.Id))
            {
                if (!CalibrationContractConstants.AttestsUpstreamSlicer(worker.CapabilitiesJson))
                {
                    continue;
                }

                ServiceAttestation service = services.First(
                    candidate => candidate.Id.ToString() == worker.ServiceId);
                if (!CalibrationSlicerAttestation.TryRead(
                        service.CapabilitiesJson,
                        out string? containerDigest,
                        out string? binarySha256))
                {
                    continue;
                }

                return new(
                    new CalibrationPinnedSlicerIdentity(
                        service.Version!,
                        CalibrationContractConstants.SlicerDistribution,
                        containerDigest,
                        binarySha256,
                        worker.Id),
                    observedVersions,
                    hasSupportedVersion);
            }

            return new(null, observedVersions, hasSupportedVersion);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Calibration generation could not evaluate worker attestation ({ExceptionType})",
                exception.GetType().Name);
            return WorkerCompatibilitySnapshot.Empty;
        }
    }

    private bool IsSplitDeployment() =>
        (_configuration.GetValue<string>("DEPLOYMENT_MODE") ??
            _configuration.GetValue<string>("Deployment:Mode"))?.ToLowerInvariant() is "split" or "microservices";

    private bool HasDeterministicCore() =>
        _serviceProvider.GetService<ICalibrationSpecificationCompiler>() is not null &&
        _serviceProvider.GetService<ICalibrationModelValidator>() is not null &&
        _serviceProvider.GetService<IOrcaCalibrationPlanCompiler>() is not null &&
        _serviceProvider.GetService<IKlipperCalibrationGcodeGenerator>() is not null &&
        _serviceProvider.GetService<ICalibrationGcodeAnnotator>() is not null &&
        _serviceProvider.GetService<ICalibrationGcodeSafetyValidator>() is not null;

    private async Task<bool> IsModelStorageRoutableAsync(CancellationToken cancellationToken)
    {
        IModelStorageResolver? resolver = _serviceProvider.GetService<IModelStorageResolver>();
        if (resolver is null)
        {
            return false;
        }

        try
        {
            // A real lookup proves the hop answers. An unknown identity must resolve to nothing
            // rather than throw, which is exactly what a healthy resolver does.
            _ = await resolver.FindOwnedAsync(Guid.Empty, Guid.Empty, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(
                "Calibration generation could not probe model storage ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> IsSliceSubmissionRoutableAsync(CancellationToken cancellationToken)
    {
        ISliceJobRepository? sliceJobs = _serviceProvider.GetService<ISliceJobRepository>();
        if (sliceJobs is null)
        {
            return false;
        }

        try
        {
            _ = await sliceJobs.GetByIdAsync(Guid.Empty, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Calibration generation could not probe slice submission ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> IsArtifactSourceRoutableAsync(CancellationToken cancellationToken)
    {
        IArtifactsService? artifacts = _serviceProvider.GetService<IArtifactsService>();
        IArtifactsRepository? repository = _serviceProvider.GetService<IArtifactsRepository>();
        if (artifacts is null || repository is null)
        {
            return false;
        }

        try
        {
            _ = await artifacts.ListByJobAsync(Guid.Empty, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(
                "Calibration generation could not probe the artifact source ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> IsPromotionOperationalAsync(CancellationToken cancellationToken)
    {
        IGcodeArtifactPromoter? promoter = _serviceProvider.GetService<IGcodeArtifactPromoter>();
        if (promoter is null)
        {
            return false;
        }

        try
        {
            GcodePromotionCapabilityDto capability = await promoter.GetCapabilityAsync(cancellationToken);
            return capability.Operational;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Calibration generation could not probe artifact promotion ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> IsOrchestrationStoreAvailableAsync(CancellationToken cancellationToken)
    {
        Farm.Infrastructure.Data.AppDbContext? dbContext =
            _serviceProvider.GetService<Farm.Infrastructure.Data.AppDbContext>();
        if (dbContext is null)
        {
            return false;
        }

        try
        {
            _ = await dbContext.CalibrationOrchestrations.AsNoTracking()
                .AnyAsync(orchestration => orchestration.Id == Guid.Empty, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Calibration generation could not probe the orchestration store ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    private sealed record ServiceAttestation(Guid Id, string? Version, string? CapabilitiesJson);

    private sealed record WorkerAttestation(Guid Id, string ServiceId, string? CapabilitiesJson);

    private sealed record WorkerCompatibilitySnapshot(
        CalibrationPinnedSlicerIdentity? PinnedIdentity,
        IReadOnlyList<string> ObservedVersions,
        bool HasSupportedVersion)
    {
        public static WorkerCompatibilitySnapshot Empty { get; } = new(null, [], false);

        public bool HasObservedVersions => ObservedVersions.Count > 0;
    }
}
