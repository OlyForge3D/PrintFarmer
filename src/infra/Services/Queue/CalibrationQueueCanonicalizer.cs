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
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<CanonicalCalibrationQueueJob> BuildAsync(
        QueuePrintJobDto request,
        GcodeFile gcode,
        QueueJobClassification classification,
        Guid? actorUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gcode);
        ArgumentNullException.ThrowIfNull(classification);

        Guid projectId = Required(classification.CalibrationProjectId, "calibration project");
        Guid attemptId = Required(classification.CalibrationAttemptId, "calibration attempt");
        Guid orchestrationId = Required(classification.CalibrationOrchestrationId, "calibration orchestration");
        Guid sourceArtifactId = Required(classification.SourceArtifactId, "source artifact");
        Guid sliceJobId = Required(classification.SliceJobId, "source slice job");

        CalibrationProject project = await _db.CalibrationProjects
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, ct)
            ?? throw Missing("calibration project");
        CalibrationAttempt attempt = await _db.CalibrationAttempts
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == attemptId, ct)
            ?? throw Missing("calibration attempt");
        CalibrationOrchestration orchestration = await _db.CalibrationOrchestrations
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == orchestrationId, ct)
            ?? throw Missing("calibration orchestration");

        // #1990: D4 (#1987) stopped populating a printer-configuration snapshot for every new
        // attempt and never regained a replacement path for the compatibility-pinning data this
        // canonicalizer requires. Fail explicitly here instead of letting the lookup below - which
        // is guaranteed to find nothing for an attempt with no snapshot FK - surface as a generic
        // "not found", which reads like data corruption rather than a known, temporary limitation.
        // This is an interim short-circuit pending #1984 (D7), not a fix for the underlying gap.
        if (attempt.PrinterConfigurationSnapshotId is null)
        {
            throw Incompatible(
                "Filament calibration dispatch requires a printer-configuration compatibility " +
                "snapshot, which is not currently populated for this attempt. This is a known " +
                "interim limitation (see issue #1990) pending #1984; the attempt cannot be queued " +
                "until that support lands.");
        }

        PrinterConfigurationSnapshot snapshot = await _db.PrinterConfigurationSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == attempt.PrinterConfigurationSnapshotId,
                ct)
            ?? throw Missing("printer configuration snapshot");

        if (actorUserId.HasValue && project.OwnerUserId != actorUserId.Value)
        {
            throw new UnauthorizedAccessException(
                "The calibration project is owned by a different user.");
        }

        if (attempt.ProjectId != project.Id ||
            orchestration.ProjectId != project.Id ||
            orchestration.AttemptId != attempt.Id ||
            snapshot.ProjectId != project.Id ||
            project.CurrentPrinterConfigurationSnapshotId != snapshot.Id ||
            snapshot.PrinterId != project.PrinterId ||
            orchestration.GcodeFileId != gcode.Id ||
            orchestration.SliceJobId != sliceJobId ||
            orchestration.FinalArtifactId != sourceArtifactId)
        {
            throw Incompatible("Calibration lineage records do not describe one immutable physical job.");
        }

        Guid assignedPrinterId = request.AssignedPrinterId
            ?? throw Incompatible("Calibration jobs require an exact assigned printer.");
        if (assignedPrinterId != project.PrinterId)
        {
            throw Incompatible("The assigned printer does not match the persisted calibration project.");
        }

        Printer printer = await _db.Printers
            .AsNoTracking()
            .Include(candidate => candidate.Toolheads)
            .SingleOrDefaultAsync(candidate => candidate.Id == assignedPrinterId, ct)
            ?? throw Missing("assigned printer");
        if (printer.ConfigurationRevision != snapshot.PrinterConfigurationRevision)
        {
            throw Incompatible("The assigned printer configuration revision is stale.");
        }

        PrinterConfigurationSnapshotDto document;
        try
        {
            document = JsonSerializer.Deserialize<PrinterConfigurationSnapshotDto>(
                snapshot.SanitizedSnapshotJson,
                SnapshotOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Incompatible("The immutable printer snapshot cannot be decoded.");
        }

        CalibrationToolheadDto snapshotToolhead = SelectToolhead(project, document);
        Toolhead currentToolhead = printer.Toolheads.SingleOrDefault(candidate =>
                candidate.Id == snapshotToolhead.Id &&
                candidate.Index == snapshotToolhead.Index)
            ?? throw Incompatible("The pinned physical toolhead no longer exists on the assigned printer.");
        decimal nozzleDiameter = snapshotToolhead.NozzleDiameter is > 0
            ? (decimal)snapshotToolhead.NozzleDiameter.Value
            : throw Incompatible("The immutable snapshot does not contain a valid nozzle diameter.");
        if (!currentToolhead.NozzleDiameter.HasValue ||
            Math.Abs((decimal)currentToolhead.NozzleDiameter.Value - nozzleDiameter) > 0.011m)
        {
            throw Incompatible("The pinned physical toolhead nozzle no longer matches the snapshot.");
        }

        Guid pinnedSpoolId = project.LocalSpoolId
            ?? project.SpoolmanSpoolId
            ?? throw Incompatible("The calibration project does not pin an exact physical spool.");
        Spool spool = await _db.Spools
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == pinnedSpoolId, ct)
            ?? throw Missing("pinned physical spool");
        if (!spool.InUse || spool.AssignedPrinterId != assignedPrinterId)
        {
            throw Incompatible("The pinned physical spool is not loaded on the assigned printer.");
        }

        string material = RequiredText(project.FilamentMaterial, "filament material");
        if (!string.Equals(spool.Material, material, StringComparison.OrdinalIgnoreCase))
        {
            throw Incompatible("The pinned spool material does not match the calibration project.");
        }

        string filamentSku = RequiredText(project.FilamentSku, "filament SKU");
        string physicalSpoolSku = RequiredText(spool.Sku, "physical spool SKU");
        if (!string.Equals(
                filamentSku,
                physicalSpoolSku,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Incompatible(
                "The physical spool SKU does not match the pinned calibration filament SKU.");
        }

        string physicalSpoolLot = RequiredText(spool.LotNumber, "physical spool lot");

        if (gcode.EstimatedFilamentWeightG is > 0 &&
            spool.WeightGrams < gcode.EstimatedFilamentWeightG.Value)
        {
            throw Incompatible("The pinned spool does not contain enough filament for the job.");
        }

        string gcodeSha = RequiredDigest(gcode.ContentSha256, "G-code content");
        if (!string.Equals(gcodeSha, orchestration.GcodeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Incompatible("The promoted G-code digest does not match the orchestration.");
        }

        string specificationSha = RequiredDigest(attempt.SpecificationSha256, "specification");
        RequireDigestMatch(orchestration.SpecificationSha256, specificationSha, "orchestration specification");
        string machineSha = RequiredDigest(snapshot.MachineProfileSha256, "machine profile");
        string processSha = RequiredDigest(snapshot.ProcessProfileSha256, "process profile");
        string filamentSha = RequiredDigest(snapshot.FilamentProfileSha256, "filament profile");
        RequireDigestMatch(gcode.SpecificationSha256, specificationSha, "specification");
        RequireDigestMatch(gcode.MachineProfileSha256, machineSha, "machine profile");
        RequireDigestMatch(gcode.ProcessProfileSha256, processSha, "process profile");
        RequireDigestMatch(gcode.FilamentProfileSha256, filamentSha, "filament profile");

        string snapshotSha = RequiredDigest(snapshot.SnapshotSha256, "printer snapshot");
        string sourceModelSha = RequiredDigest(gcode.SourceModelSha256, "source model");
        string manifestSha = RequiredDigest(
            gcode.CalibrationManifestSha256 ?? orchestration.ManifestSha256,
            "calibration manifest");
        string engine = RequiredText(gcode.SlicerEngineName, "slicer engine");
        string distribution = RequiredText(gcode.SlicerDistribution, "slicer distribution");
        string version = RequiredText(gcode.PinnedSlicerVersion, "slicer version");
        string containerDigest = RequiredText(
            gcode.SlicerContainerDigest ?? orchestration.SlicerContainerDigest,
            "slicer container digest");
        if (gcode.FileSizeBytes <= 0)
        {
            throw Incompatible("The promoted G-code byte size is missing or invalid.");
        }

        double estimatedFilamentUsage = gcode.EstimatedFilamentWeightG is > 0
            ? gcode.EstimatedFilamentWeightG.Value
            : throw Incompatible(
                "The promoted G-code does not contain a valid filament-consumption estimate.");

        double dimensionX = RequiredDimension(gcode.ObjectDimensionX, "X");
        double dimensionY = RequiredDimension(gcode.ObjectDimensionY, "Y");
        double dimensionZ = RequiredDimension(gcode.ObjectDimensionZ, "Z");
        if (!printer.MaxBuildVolumeX.HasValue ||
            !printer.MaxBuildVolumeY.HasValue ||
            !printer.MaxBuildVolumeZ.HasValue ||
            dimensionX > printer.MaxBuildVolumeX.Value ||
            dimensionY > printer.MaxBuildVolumeY.Value ||
            dimensionZ > printer.MaxBuildVolumeZ.Value)
        {
            throw Incompatible("The promoted G-code dimensions cannot be proven to fit the assigned printer.");
        }

        if (gcode.PrinterModelId != printer.ModelId ||
            !string.Equals(snapshot.SlicerEngine, engine, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerDistribution, distribution, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerVersion, version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.SlicerContainerDigest, containerDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(orchestration.SlicerContainerDigest, containerDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw Incompatible("The promoted G-code does not match the immutable model and slicer snapshot.");
        }

        RequireDigestMatch(orchestration.ManifestSha256, manifestSha, "calibration manifest");
        if (snapshot.FirmwareFamily != PrinterFirmwareFamily.Klipper ||
            snapshot.GcodeDialect != PrinterGcodeDialect.Klipper ||
            !string.Equals(engine, "OrcaSlicer", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(distribution, "upstream", StringComparison.OrdinalIgnoreCase))
        {
            throw Incompatible(
                "Calibration dispatch requires explicit Klipper firmware, Klipper G-code, and upstream OrcaSlicer.");
        }

        string[] capabilities = BuildRequiredCapabilities(document, snapshotToolhead);
        var canonical = new CanonicalCalibrationQueueJob
        {
            JobKind = JobKind.FilamentCalibration,
            Copies = 1,
            GcodeFileId = gcode.Id,
            AssignedPrinterId = assignedPrinterId,
            Priority = request.Priority,
            CalibrationProjectId = project.Id,
            CalibrationAttemptId = attempt.Id,
            CalibrationConfigSnapshotId = snapshot.Id,
            CalibrationOrchestrationId = orchestration.Id,
            SourceArtifactId = sourceArtifactId,
            SliceJobId = sliceJobId,
            GcodeContentSha256 = gcodeSha,
            GcodeFileSizeBytes = gcode.FileSizeBytes,
            EstimatedFilamentUsage = estimatedFilamentUsage,
            RequiredFirmwareFamily = snapshot.FirmwareFamily,
            RequiredGcodeDialect = snapshot.GcodeDialect,
            RequiredSlicerEngine = engine,
            RequiredSlicerDistribution = distribution,
            RequiredSlicerVersion = version,
            RequiredSlicerContainerDigest = containerDigest,
            SpecificationSha256 = specificationSha,
            MachineProfileSha256 = machineSha,
            ProcessProfileSha256 = processSha,
            FilamentProfileSha256 = filamentSha,
            PrinterConfigSnapshotSha256 = snapshotSha,
            PinnedPrinterConfigRevision = snapshot.PrinterConfigurationRevision,
            PinnedPrinterModelId = printer.ModelId,
            PinnedToolheadId = snapshotToolhead.Id,
            PinnedToolheadIndex = snapshotToolhead.Index,
            PinnedSpoolId = spool.Id,
            PinnedFilamentSku = filamentSku,
            PinnedFilamentLotNumber = physicalSpoolLot,
            FilamentSnapshotSha256 = Sha256(
                RequiredJson(project.FilamentSnapshotJson, "filament snapshot")),
            SourceModelSha256 = sourceModelSha,
            CalibrationManifestSha256 = manifestSha,
            RequiredNozzleDiameter = nozzleDiameter,
            RequiredMaterialType = material,
            RequiredCapabilities = capabilities,
            PinnedObjectDimensionX = dimensionX,
            PinnedObjectDimensionY = dimensionY,
            PinnedObjectDimensionZ = dimensionZ,
            FilamentName = RequiredText(project.FilamentProductName, "filament product"),
            FilamentVendor = project.FilamentVendor,
            FilamentColor = project.FilamentColor,
        };

        RejectTamperedClientFields(request, canonical);
        return canonical;
    }

    private static CalibrationToolheadDto SelectToolhead(
        CalibrationProject project,
        PrinterConfigurationSnapshotDto snapshot)
    {
        CalibrationToolheadDto? selected = project.SelectedToolheadId.HasValue
            ? snapshot.Toolheads.FirstOrDefault(candidate =>
                candidate.Id == project.SelectedToolheadId.Value &&
                (!project.SelectedToolheadIndex.HasValue ||
                 candidate.Index == project.SelectedToolheadIndex.Value))
            : project.SelectedToolheadIndex.HasValue
                ? snapshot.Toolheads.FirstOrDefault(candidate =>
                    candidate.Index == project.SelectedToolheadIndex.Value)
                : snapshot.Toolheads.FirstOrDefault(candidate => candidate.IsPrimary);
        return selected ?? throw Incompatible(
            "The immutable snapshot does not contain the selected physical toolhead.");
    }

    private static string[] BuildRequiredCapabilities(
        PrinterConfigurationSnapshotDto snapshot,
        CalibrationToolheadDto toolhead)
    {
        List<string> capabilities = [];
        AddIf(capabilities, snapshot.HasHeatedBed == true, "heated_bed");
        AddIf(capabilities, snapshot.HasEnclosure == true, "enclosure");
        AddIf(capabilities, snapshot.HasHeatedChamber == true, "heated_chamber");
        AddIf(capabilities, toolhead.NozzleIsHardened == true, "hardened_nozzle");
        AddIf(capabilities, toolhead.IsDirectDrive == true, "direct_drive");
        return capabilities
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddIf(List<string> values, bool condition, string value)
    {
        if (condition)
        {
            values.Add(value);
        }
    }

    private static void RejectTamperedClientFields(
        QueuePrintJobDto request,
        CanonicalCalibrationQueueJob canonical)
    {
        bool mismatch =
            Different(request.CalibrationProjectId, canonical.CalibrationProjectId) ||
            Different(request.CalibrationAttemptId, canonical.CalibrationAttemptId) ||
            Different(request.CalibrationConfigSnapshotId, canonical.CalibrationConfigSnapshotId) ||
            Different(request.CalibrationOrchestrationId, canonical.CalibrationOrchestrationId) ||
            Different(request.SourceArtifactId, canonical.SourceArtifactId) ||
            Different(request.GcodeContentSha256, canonical.GcodeContentSha256) ||
            Different(request.RequiredFirmwareFamily, canonical.RequiredFirmwareFamily) ||
            Different(request.RequiredGcodeDialect, canonical.RequiredGcodeDialect) ||
            Different(request.RequiredSlicerEngine, canonical.RequiredSlicerEngine) ||
            Different(request.RequiredSlicerDistribution, canonical.RequiredSlicerDistribution) ||
            Different(request.RequiredSlicerVersion, canonical.RequiredSlicerVersion) ||
            Different(request.RequiredSlicerContainerDigest, canonical.RequiredSlicerContainerDigest) ||
            Different(request.SpecificationSha256, canonical.SpecificationSha256) ||
            Different(request.MachineProfileSha256, canonical.MachineProfileSha256) ||
            Different(request.ProcessProfileSha256, canonical.ProcessProfileSha256) ||
            Different(request.FilamentProfileSha256, canonical.FilamentProfileSha256) ||
            Different(request.PrinterConfigSnapshotSha256, canonical.PrinterConfigSnapshotSha256) ||
            Different(request.PinnedPrinterConfigRevision, canonical.PinnedPrinterConfigRevision) ||
            Different(request.RequiredNozzleDiameter, canonical.RequiredNozzleDiameter) ||
            Different(request.RequiredMaterialType, canonical.RequiredMaterialType) ||
            (request.RequiredCapabilities is { Length: > 0 } &&
             !NormalizeCapabilities(request.RequiredCapabilities)
                 .SequenceEqual(canonical.RequiredCapabilities, StringComparer.OrdinalIgnoreCase));

        if (mismatch)
        {
            throw Incompatible(
                "Client-supplied calibration fields do not match the authoritative persisted resources.");
        }
    }

    private static bool Different<T>(T? supplied, T canonical)
        where T : struct =>
        supplied.HasValue && !EqualityComparer<T>.Default.Equals(supplied.Value, canonical);

    private static bool Different(string? supplied, string canonical) =>
        supplied is not null &&
        !string.Equals(supplied.Trim(), canonical, StringComparison.OrdinalIgnoreCase);

    private static string[] NormalizeCapabilities(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static Guid Required(Guid? value, string name) =>
        value ?? throw Incompatible($"The promoted artifact is missing its {name} identity.");

    private static string RequiredText(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw Incompatible($"The authoritative {name} is missing.")
            : value.Trim();

    private static string RequiredJson(string? value, string name)
    {
        string json = RequiredText(value, name);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new JsonException();
            }
        }
        catch (JsonException)
        {
            throw Incompatible($"The authoritative {name} is invalid.");
        }

        return json;
    }

    private static double RequiredDimension(double? value, string axis) =>
        value is > 0 and < double.PositiveInfinity
            ? value.Value
            : throw Incompatible(
                $"The promoted G-code is missing a valid {axis} object dimension.");

    private static string RequiredDigest(string? value, string name)
    {
        string normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized
            : throw Incompatible($"The authoritative {name} SHA-256 is missing or invalid.");
    }

    private static void RequireDigestMatch(string? actual, string expected, string name)
    {
        if (!string.Equals(RequiredDigest(actual, name), expected, StringComparison.Ordinal))
        {
            throw Incompatible($"The promoted {name} digest does not match the immutable snapshot.");
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static CalibrationQueueResourceNotFoundException Missing(string resource) =>
        new($"The authoritative {resource} was not found.");

    private static CalibrationQueueIncompatibleException Incompatible(string message) =>
        new(message);
}
