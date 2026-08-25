// <copyright file="CalibrationQueueCanonicalizer.cs" company="PlaceholderCompany">
// SPDX-License-Identifier: AGPL-3.0-only
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Raised when authoritative calibration resources are missing.
/// </summary>
public sealed class CalibrationQueueResourceNotFoundException : Exception
{
    public CalibrationQueueResourceNotFoundException()
    {
    }

    public CalibrationQueueResourceNotFoundException(string message)
        : base(message)
    {
    }

    public CalibrationQueueResourceNotFoundException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when persisted calibration resources are incomplete, stale, or mutually inconsistent.
/// </summary>
public sealed class CalibrationQueueIncompatibleException : ValidationException
{
    public CalibrationQueueIncompatibleException()
    {
    }

    public CalibrationQueueIncompatibleException(string message)
        : base(message)
    {
    }

    public CalibrationQueueIncompatibleException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One server-derived immutable calibration queue object. Its serialized representation is
/// the idempotency hash input and every property is copied unchanged into <see cref="PrintJob"/>.
/// </summary>
public sealed record CanonicalCalibrationQueueJob
{
    public required JobKind JobKind { get; init; }

    public required int Copies { get; init; }

    public required Guid GcodeFileId { get; init; }

    public required Guid AssignedPrinterId { get; init; }

    public required PrintJobPriority Priority { get; init; }

    public required Guid CalibrationProjectId { get; init; }

    public required Guid CalibrationAttemptId { get; init; }

    public required Guid CalibrationConfigSnapshotId { get; init; }

    public required Guid CalibrationOrchestrationId { get; init; }

    public required Guid SourceArtifactId { get; init; }

    public required Guid SliceJobId { get; init; }

    public required string GcodeContentSha256 { get; init; }

    public required long GcodeFileSizeBytes { get; init; }

    public required double EstimatedFilamentUsage { get; init; }

    public required PrinterFirmwareFamily RequiredFirmwareFamily { get; init; }

    public required PrinterGcodeDialect RequiredGcodeDialect { get; init; }

    public required string RequiredSlicerEngine { get; init; }

    public required string RequiredSlicerDistribution { get; init; }

    public required string RequiredSlicerVersion { get; init; }

    public required string RequiredSlicerContainerDigest { get; init; }

    public required string SpecificationSha256 { get; init; }

    public required string MachineProfileSha256 { get; init; }

    public required string ProcessProfileSha256 { get; init; }

    public required string FilamentProfileSha256 { get; init; }

    public required string PrinterConfigSnapshotSha256 { get; init; }

    public required long PinnedPrinterConfigRevision { get; init; }

    public required Guid PinnedPrinterModelId { get; init; }

    public required Guid PinnedToolheadId { get; init; }

    public required int PinnedToolheadIndex { get; init; }

    public required Guid PinnedSpoolId { get; init; }

    public required string PinnedFilamentSku { get; init; }

    public required string PinnedFilamentLotNumber { get; init; }

    public required string FilamentSnapshotSha256 { get; init; }

    public required string SourceModelSha256 { get; init; }

    public required string CalibrationManifestSha256 { get; init; }

    public required decimal RequiredNozzleDiameter { get; init; }

    public required string RequiredMaterialType { get; init; }

    public required string[] RequiredCapabilities { get; init; }

    public double? PinnedObjectDimensionX { get; init; }

    public double? PinnedObjectDimensionY { get; init; }

    public double? PinnedObjectDimensionZ { get; init; }

    public required string FilamentName { get; init; }

    public string? FilamentVendor { get; init; }

    public string? FilamentColor { get; init; }

    public string ComputeRequestSha256(string idempotencyScope)
    {
        string canonical = JsonSerializer.Serialize(new
        {
            idempotencyScope,
            value = this,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>
/// Builds calibration queue input exclusively from durable server-side resources.
/// </summary>
public sealed class CalibrationQueueCanonicalizer(AppDbContext db)
{
    public Task<CanonicalCalibrationQueueJob> BuildAsync(
        QueuePrintJobDto request,
        GcodeFile gcode,
        QueueJobClassification classification,
        Guid? actorUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gcode);
        ArgumentNullException.ThrowIfNull(classification);

        // Path D (#1980): PrinterConfigurationSnapshot and the CalibrationOrchestration
        // generator-attestation columns were deleted. There is no authoritative snapshot left
        // to canonicalize against, so calibration queue submission is unsupported until the
        // filament-calibration saga (D7) replaces this pipeline. Every attempt created after
        // Path D (#1981) already has a null snapshot reference, so this always failed here
        // anyway; the failure is now immediate instead of after several redundant lookups.
        throw Missing("printer configuration snapshot");
    }

    private static CalibrationQueueResourceNotFoundException Missing(string resource) =>
        new($"The authoritative {resource} was not found.");
}
