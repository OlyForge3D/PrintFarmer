using System.Data.Common;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Api.Services;

/// <summary>
/// Answers worker/version compatibility for the main API's calibration generation capability probe
/// (issue #1848), reading this process's own <see cref="SlicerDbContext"/>.
/// </summary>
public interface ISlicerHostWorkerCompatibilityService
{
    /// <summary>
    /// Finds the eligible pinned worker identity and observed upstream OrcaSlicer versions.
    /// </summary>
    /// <param name="requiredSlicerVersion">
    /// An optional exact slicer version the eligible worker must report, or <see langword="null"/> to
    /// accept any allow-listed supported version.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compatibility snapshot.</returns>
    Task<WorkerCompatibilitySnapshotDto> GetWorkerCompatibilityAsync(
        string? requiredSlicerVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ISlicerHostWorkerCompatibilityService"/>.
/// </summary>
/// <remarks>
/// This is a faithful port of the query the deleted calibration generation saga's capability
/// probe (removed by #1979) used to run against a local <c>IDbContextFactory&lt;SlicerDbContext&gt;</c>
/// in a monolith deployment. In a split/microservices deployment the main API has no such factory,
/// because this host owns <see cref="SlicerDbContext"/> in its own process; the main API instead
/// calls this endpoint over an authenticated internal HTTP hop guarded by
/// <c>WorkerAuth:SharedKey</c> (issue #1848). Keeping the query identical here is what makes both
/// topologies report the same answer.
/// </remarks>
public sealed class SlicerHostWorkerCompatibilityService(
    IDbContextFactory<SlicerDbContext> slicerContextFactory,
    ILogger<SlicerHostWorkerCompatibilityService> logger,
    CalibrationSlicerCompatibilityPolicy? compatibilityPolicy = null)
    : ISlicerHostWorkerCompatibilityService
{
    private static readonly TimeSpan WorkerHeartbeatFreshness = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<SlicerDbContext> _slicerContextFactory =
        slicerContextFactory ?? throw new ArgumentNullException(nameof(slicerContextFactory));

    private readonly ILogger<SlicerHostWorkerCompatibilityService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly CalibrationSlicerCompatibilityPolicy _compatibilityPolicy =
        compatibilityPolicy ?? CalibrationSlicerCompatibilityPolicy.Default;

    /// <inheritdoc />
    public async Task<WorkerCompatibilitySnapshotDto> GetWorkerCompatibilityAsync(
        string? requiredSlicerVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SlicerDbContext db =
                await _slicerContextFactory.CreateDbContextAsync(cancellationToken);
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
                    (requiredSlicerVersion is null ||
                     string.Equals(
                         service.Version,
                         requiredSlicerVersion,
                         StringComparison.Ordinal)) &&
                    CalibrationContractConstants.AttestsUpstreamSlicer(service.CapabilitiesJson) &&
                    WorkerCompatibilitySlicerAttestation.TryRead(service.CapabilitiesJson, out _, out _))
                .Select(service => service.Id.ToString())
                .ToArray();
            if (eligibleServiceIds.Length == 0)
            {
                return new WorkerCompatibilitySnapshotDto(null, observedVersions, hasSupportedVersion);
            }

            // Identity/attestation selection answers "is there a worker in good standing that attests
            // an allow-listed upstream slicer identity", not "is there free capacity right now". A
            // worker claiming and actively running the very job this probe is being asked about is
            // healthy, online, authenticated and correctly attested; it must stay pinned-available
            // while busy. Whether a *new* attempt can be scheduled onto it is a queue/claim concern
            // handled where slice jobs are submitted and dispatched, not here.
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
                    worker.Version,
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
                if (!string.Equals(worker.Version, service.Version, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!WorkerCompatibilitySlicerAttestation.TryRead(
                        service.CapabilitiesJson,
                        out string? containerDigest,
                        out string? binarySha256))
                {
                    continue;
                }

                return new WorkerCompatibilitySnapshotDto(
                    new WorkerCompatibilityPinnedIdentityDto(
                        service.Version!,
                        CalibrationContractConstants.SlicerDistribution,
                        containerDigest,
                        binarySha256,
                        worker.Id),
                    observedVersions,
                    hasSupportedVersion);
            }

            return new WorkerCompatibilitySnapshotDto(null, observedVersions, hasSupportedVersion);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            _logger.LogWarning(
                "Worker compatibility probe could not evaluate worker attestation ({ExceptionType})",
                exception.GetType().Name);
            return WorkerCompatibilitySnapshotDto.Empty;
        }
    }

    private sealed record ServiceAttestation(Guid Id, string? Version, string? CapabilitiesJson);

    private sealed record WorkerAttestation(
        Guid Id,
        string ServiceId,
        string? Version,
        string? CapabilitiesJson);
}
