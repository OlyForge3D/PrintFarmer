using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Hard, shared safety gates evaluated by every start path before a printer is claimed
/// (issue #900, defect 7 and 8).
///
/// These checks are deliberately FAIL-CLOSED: a missing input is a rejection, never an
/// implicit pass. They are pure functions so the claim service and the bed-clear
/// acknowledgement service enforce exactly the same rules and can never drift.
/// </summary>
public static class DispatchSafetyGates
{
    /// <summary>Nozzle diameter comparison tolerance in millimetres.</summary>
    private const decimal NozzleToleranceMm = 0.011m;

    /// <summary>Maps a dispatch failure code to a durable calibration blocked reason.</summary>
    public static JobBlockedReasonCode? MapBlockedReason(string? errorCode) =>
        errorCode switch
        {
            "firmware_family_mismatch" => JobBlockedReasonCode.FirmwareFamilyMismatch,
            "gcode_dialect_mismatch" => JobBlockedReasonCode.GcodeDialectMismatch,
            "slicer_tuple_mismatch" => JobBlockedReasonCode.SlicerTupleMismatch,
            "gcode_hash_missing" or
            "gcode_hash_mismatch" or
            "gcode_hash_unverifiable" or
            "gcode_byte_hash_mismatch" or
            "gcode_size_mismatch" or
            "gcode_byte_size_mismatch" or
            "gcode_file_missing" => JobBlockedReasonCode.ContentHashMismatch,
            "printer_config_revision_missing" or
            "printer_config_revision_stale" => JobBlockedReasonCode.PrinterConfigRevisionStale,
            "calibration_record_invalid" or
            "calibration_record_mismatch" => JobBlockedReasonCode.CalibrationRecordInvalid,
            "filament_spool_missing" or
            "filament_spool_unknown" or
            "filament_spool_mismatch" or
            "filament_material_missing" or
            "filament_material_unknown" or
            "filament_material_mismatch" or
            "filament_insufficient" => JobBlockedReasonCode.FilamentCheckFailed,
            "capabilities_unsatisfied" => JobBlockedReasonCode.MissingRequiredCapability,
            "compatibility_incomplete" or
            "printer_model_mismatch" or
            "toolhead_mismatch" or
            "hardware_evidence_incomplete" or
            "build_volume_exceeded" or
            "nozzle_unknown" or
            "nozzle_mismatch" or
            "gcode_metadata_mismatch" => JobBlockedReasonCode.HardCompatibilityFailure,
            _ => null,
        };

    /// <summary>
    /// Verifies advertised capabilities, nozzle diameter, printer model and build volume.
    /// </summary>
    /// <param name="job">Job being dispatched.</param>
    /// <param name="printer">Target printer including its toolheads.</param>
    /// <returns>A failure result, or <see langword="null"/> when all hardware gates pass.</returns>
    public static DispatchClaimResult? EvaluateHardware(PrintJob job, Printer printer)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printer);

        // --- Advertised capabilities ---
        if (job.RequiredCapabilities is { Length: > 0 })
        {
            HashSet<string> advertised = BuildAdvertisedCapabilities(printer);

            List<string> missing = job.RequiredCapabilities
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Where(c => !advertised.Contains(c.Trim(), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (missing.Count > 0)
            {
                return DispatchClaimResult.Fail(
                    "capabilities_unsatisfied",
                    $"Printer does not advertise required capabilities: {string.Join(", ", missing)}.");
            }
        }

        // --- Nozzle diameter (toolhead-aware) ---
        if (job.RequiredNozzleDiameter is { } requiredNozzle && requiredNozzle > 0m)
        {
            List<decimal> available = printer.Toolheads
                .Where(t => t.NozzleDiameter.HasValue)
                .Select(t => (decimal)t.NozzleDiameter!.Value)
                .ToList();

            if (available.Count == 0 && printer.NozzleDiameter.HasValue)
            {
                available.Add((decimal)printer.NozzleDiameter.Value);
            }

            if (available.Count == 0)
            {
                return DispatchClaimResult.Fail(
                    "nozzle_unknown",
                    "The job pins a required nozzle diameter but the printer advertises none. Dispatch fails closed.");
            }

            if (!available.Any(d => Math.Abs(d - requiredNozzle) <= NozzleToleranceMm))
            {
                return DispatchClaimResult.Fail(
                    "nozzle_mismatch",
                    $"No toolhead matches the required nozzle diameter {requiredNozzle:0.00}mm.");
            }
        }

        // --- Build volume ---
        if (job.GcodeFile is { } gcode)
        {
            if (job.JobKind == JobKind.FilamentCalibration)
            {
                if (!job.PinnedPrinterModelId.HasValue ||
                    job.PinnedPrinterModelId.Value != printer.ModelId)
                {
                    return DispatchClaimResult.Fail(
                        "printer_model_mismatch",
                        "The assigned printer model does not match the job's pinned model.");
                }

                if (!job.PinnedToolheadId.HasValue ||
                    !job.PinnedToolheadIndex.HasValue ||
                    !printer.Toolheads.Any(toolhead =>
                        toolhead.Id == job.PinnedToolheadId.Value &&
                        toolhead.Index == job.PinnedToolheadIndex.Value))
                {
                    return DispatchClaimResult.Fail(
                        "toolhead_mismatch",
                        "The assigned printer no longer contains the exact pinned physical toolhead.");
                }

                if (job.RequiredNozzleDiameter is not > 0 ||
                    job.RequiredCapabilities is null ||
                    job.PinnedObjectDimensionX is not > 0 ||
                    job.PinnedObjectDimensionY is not > 0 ||
                    job.PinnedObjectDimensionZ is not > 0 ||
                    gcode.ObjectDimensionX is not > 0 ||
                    gcode.ObjectDimensionY is not > 0 ||
                    gcode.ObjectDimensionZ is not > 0 ||
                    printer.MaxBuildVolumeX is not > 0 ||
                    printer.MaxBuildVolumeY is not > 0 ||
                    printer.MaxBuildVolumeZ is not > 0)
                {
                    return DispatchClaimResult.Fail(
                        "hardware_evidence_incomplete",
                        "Calibration dispatch requires explicit nozzle, capability, object-dimension, and build-volume evidence.");
                }

                const double DimensionToleranceMm = 0.0001;
                if (Math.Abs(
                        gcode.ObjectDimensionX.Value -
                        job.PinnedObjectDimensionX.Value) > DimensionToleranceMm ||
                    Math.Abs(
                        gcode.ObjectDimensionY.Value -
                        job.PinnedObjectDimensionY.Value) > DimensionToleranceMm ||
                    Math.Abs(
                        gcode.ObjectDimensionZ.Value -
                        job.PinnedObjectDimensionZ.Value) > DimensionToleranceMm)
                {
                    return DispatchClaimResult.Fail(
                        "gcode_metadata_mismatch",
                        "The G-code object dimensions changed after the job was queued.");
                }
            }

            if (gcode.ObjectDimensionX is { } dx && printer.MaxBuildVolumeX is { } bx && dx > bx)
            {
                return DispatchClaimResult.Fail(
                    "build_volume_exceeded",
                    $"Object X dimension {dx:0.0}mm exceeds the printer build volume {bx:0.0}mm.");
            }

            if (gcode.ObjectDimensionY is { } dy && printer.MaxBuildVolumeY is { } by && dy > by)
            {
                return DispatchClaimResult.Fail(
                    "build_volume_exceeded",
                    $"Object Y dimension {dy:0.0}mm exceeds the printer build volume {by:0.0}mm.");
            }

            if (gcode.ObjectDimensionZ is { } dz && printer.MaxBuildVolumeZ is { } bz && dz > bz)
            {
                return DispatchClaimResult.Fail(
                    "build_volume_exceeded",
                    $"Object Z dimension {dz:0.0}mm exceeds the printer build volume {bz:0.0}mm.");
            }

            // --- Printer model ---
            if (gcode.PrinterModelId.HasValue &&
                printer.ModelId != Guid.Empty &&
                gcode.PrinterModelId.Value != printer.ModelId &&
                job.JobKind == JobKind.FilamentCalibration)
            {
                return DispatchClaimResult.Fail(
                    "printer_model_mismatch",
                    "The calibration artifact was sliced for a different printer model.");
            }
        }

        return null;
    }

    /// <summary>
    /// Hard filament / SKU / spool gate.
    ///
    /// Calibration jobs FAIL CLOSED: the exact spool the job was created against must still
    /// be loaded. Standard jobs are rejected when the loaded material contradicts the job's
    /// required material — an unknown loaded material is only tolerated for standard jobs.
    /// </summary>
    /// <param name="job">Job being dispatched.</param>
    /// <param name="printer">Target printer including its toolheads.</param>
    /// <returns>A failure result, or <see langword="null"/> when the filament gate passes.</returns>
    public static DispatchClaimResult? EvaluateFilament(PrintJob job, Printer printer)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printer);

        bool isCalibration = job.JobKind == JobKind.FilamentCalibration;

        List<int> loadedSpools = printer.Toolheads
            .Where(t => t.CurrentSpoolId.HasValue)
            .Select(t => t.CurrentSpoolId!.Value)
            .ToList();

        if (printer.CurrentSpoolId.HasValue)
        {
            loadedSpools.Add(printer.CurrentSpoolId.Value);
        }

        List<string> loadedMaterials = printer.Toolheads
            .Where(t => !string.IsNullOrWhiteSpace(t.CurrentMaterial))
            .Select(t => t.CurrentMaterial!)
            .ToList();

        if (!string.IsNullOrWhiteSpace(printer.CurrentMaterial))
        {
            loadedMaterials.Add(printer.CurrentMaterial);
        }

        // --- Spool identity ---
        if (job.SpoolmanSpoolId is { } requiredSpool)
        {
            if (loadedSpools.Count == 0)
            {
                return isCalibration
                    ? DispatchClaimResult.Fail(
                        "filament_spool_unknown",
                        "Calibration dispatch requires the pinned spool to be loaded, but the printer reports no loaded spool.")
                    : null;
            }

            if (!loadedSpools.Contains(requiredSpool))
            {
                return DispatchClaimResult.Fail(
                    "filament_spool_mismatch",
                    $"The job pins spool {requiredSpool} but the printer has a different spool loaded.");
            }
        }
        else if (isCalibration && !job.PinnedSpoolId.HasValue)
        {
            return DispatchClaimResult.Fail(
                "filament_spool_missing",
                "Calibration jobs must pin an exact physical spool.");
        }

        // --- Material / SKU ---
        if (!string.IsNullOrWhiteSpace(job.RequiredMaterialType))
        {
            if (loadedMaterials.Count == 0)
            {
                return isCalibration
                    ? DispatchClaimResult.Fail(
                        "filament_material_unknown",
                        "Calibration dispatch requires a known loaded material, but the printer reports none.")
                    : null;
            }

            bool materialMatches = loadedMaterials.Any(m =>
                string.Equals(m.Trim(), job.RequiredMaterialType.Trim(), StringComparison.OrdinalIgnoreCase));

            if (!materialMatches)
            {
                string materialDetail =
                    $"The job requires material '{job.RequiredMaterialType}' but the printer has " +
                    $"'{string.Join(", ", loadedMaterials)}' loaded.";
                return DispatchClaimResult.Fail("filament_material_mismatch", materialDetail);
            }
        }
        else if (isCalibration)
        {
            return DispatchClaimResult.Fail(
                "filament_material_missing",
                "Calibration jobs must pin the required material type.");
        }

        return null;
    }

    /// <summary>
    /// Complete calibration compatibility tuple, hash and lineage verification.
    /// All fields must be explicitly set (non-null, non-Unknown) — null fields are never
    /// inferred from manufacturer/model/backend.
    /// </summary>
    /// <param name="job">Calibration job being dispatched.</param>
    /// <param name="printer">Target printer.</param>
    /// <returns>A failure result, or <see langword="null"/> when the calibration gates pass.</returns>
    public static DispatchClaimResult? EvaluateCalibrationCompatibility(PrintJob job, Printer printer)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(printer);

        if (!job.RequiredFirmwareFamily.HasValue || job.RequiredFirmwareFamily == PrinterFirmwareFamily.Unknown)
        {
            return DispatchClaimResult.Fail(
                "compatibility_incomplete",
                "Calibration job is missing required firmware family. Null or Unknown compatibility fields are not permitted.");
        }

        if (!job.RequiredGcodeDialect.HasValue || job.RequiredGcodeDialect == PrinterGcodeDialect.Unknown)
        {
            return DispatchClaimResult.Fail(
                "compatibility_incomplete",
                "Calibration job is missing required G-code dialect. Null or Unknown compatibility fields are not permitted.");
        }

        if (string.IsNullOrWhiteSpace(job.RequiredSlicerEngine))
        {
            return DispatchClaimResult.Fail(
                "compatibility_incomplete",
                "Calibration job is missing required slicer engine.");
        }

        if (string.IsNullOrWhiteSpace(job.RequiredSlicerDistribution))
        {
            return DispatchClaimResult.Fail(
                "compatibility_incomplete",
                "Calibration job is missing required slicer distribution.");
        }

        if (string.IsNullOrWhiteSpace(job.RequiredSlicerVersion))
        {
            return DispatchClaimResult.Fail(
                "compatibility_incomplete",
                "Calibration job is missing required slicer version.");
        }

        if (printer.FirmwareFamily != job.RequiredFirmwareFamily)
        {
            return DispatchClaimResult.Fail(
                "firmware_family_mismatch",
                $"Job requires firmware family '{job.RequiredFirmwareFamily}' but printer has '{printer.FirmwareFamily}'.");
        }

        if (printer.GcodeDialect != job.RequiredGcodeDialect)
        {
            return DispatchClaimResult.Fail(
                "gcode_dialect_mismatch",
                $"Job requires G-code dialect '{job.RequiredGcodeDialect}' but printer has '{printer.GcodeDialect}'.");
        }

        if (!string.Equals(printer.CalibrationSlicerEngine, job.RequiredSlicerEngine, StringComparison.OrdinalIgnoreCase))
        {
            return DispatchClaimResult.Fail(
                "slicer_tuple_mismatch",
                $"Job requires slicer engine '{job.RequiredSlicerEngine}' but printer is configured for '{printer.CalibrationSlicerEngine}'.");
        }

        if (!string.Equals(printer.CalibrationSlicerDistribution, job.RequiredSlicerDistribution, StringComparison.OrdinalIgnoreCase))
        {
            return DispatchClaimResult.Fail(
                "slicer_tuple_mismatch",
                $"Job requires slicer distribution '{job.RequiredSlicerDistribution}' but printer is configured for '{printer.CalibrationSlicerDistribution}'.");
        }

        if (!string.Equals(printer.CalibrationSlicerVersion, job.RequiredSlicerVersion, StringComparison.OrdinalIgnoreCase))
        {
            return DispatchClaimResult.Fail(
                "slicer_tuple_mismatch",
                $"Job requires slicer version '{job.RequiredSlicerVersion}' but printer is configured for '{printer.CalibrationSlicerVersion}'.");
        }

        if (!job.PinnedPrinterConfigRevision.HasValue)
        {
            return DispatchClaimResult.Fail(
                "printer_config_revision_missing",
                "Calibration jobs must pin the printer configuration revision they were created against.");
        }

        if (printer.ConfigurationRevision != job.PinnedPrinterConfigRevision.Value)
        {
            return DispatchClaimResult.Fail(
                "printer_config_revision_stale",
                $"Printer configuration revision {printer.ConfigurationRevision} does not match the pinned revision {job.PinnedPrinterConfigRevision}.");
        }

        // Calibration lineage completeness: all provenance IDs must be non-null.
        if (!job.CalibrationProjectId.HasValue ||
            !job.CalibrationAttemptId.HasValue ||
            !job.CalibrationConfigSnapshotId.HasValue ||
            !job.CalibrationOrchestrationId.HasValue)
        {
            const string LineageDetail =
                "Calibration job is missing required provenance IDs (project, attempt, snapshot, or orchestration). " +
                "The job must be created through the authoritative calibration creation path.";
            return DispatchClaimResult.Fail("calibration_lineage_incomplete", LineageDetail);
        }

        // Specification and profile hashes are classified before the broader physical
        // tuple so clients receive the most precise immutable-input failure.
        if (string.IsNullOrWhiteSpace(job.SpecificationSha256) ||
            string.IsNullOrWhiteSpace(job.MachineProfileSha256) ||
            string.IsNullOrWhiteSpace(job.ProcessProfileSha256) ||
            string.IsNullOrWhiteSpace(job.FilamentProfileSha256))
        {
            const string HashesDetail =
                "Calibration job is missing required specification or profile hashes. " +
                "All hashes must be provided at job creation time.";
            return DispatchClaimResult.Fail("calibration_hashes_incomplete", HashesDetail);
        }

        if (!job.PinnedPrinterModelId.HasValue ||
            !job.PinnedToolheadId.HasValue ||
            !job.PinnedToolheadIndex.HasValue ||
            !job.PinnedSpoolId.HasValue ||
            string.IsNullOrWhiteSpace(job.FilamentSnapshotSha256) ||
            string.IsNullOrWhiteSpace(job.SourceModelSha256) ||
            string.IsNullOrWhiteSpace(job.CalibrationManifestSha256) ||
            !job.PinnedGcodeFileSizeBytes.HasValue)
        {
            return DispatchClaimResult.Fail(
                "physical_inputs_incomplete",
                "Calibration job is missing pinned model, toolhead, spool, byte-count, or immutable digest inputs.");
        }

        if (job.GcodeFile is null ||
            job.PinnedGcodeFileSizeBytes != job.GcodeFile.FileSizeBytes ||
            !string.Equals(
                job.SourceModelSha256,
                job.GcodeFile.SourceModelSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                job.CalibrationManifestSha256,
                job.GcodeFile.CalibrationManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return DispatchClaimResult.Fail(
                "gcode_metadata_mismatch",
                "The promoted G-code size, source-model digest, or manifest digest changed after queue creation.");
        }

        // Artifact lineage must agree with the job's pinned lineage — a promoted artifact
        // from a different attempt must never be printed under this job's provenance.
        if (job.GcodeFile is { } gcode)
        {
            if (gcode.CalibrationAttemptId.HasValue &&
                gcode.CalibrationAttemptId != job.CalibrationAttemptId)
            {
                return DispatchClaimResult.Fail(
                    "calibration_lineage_mismatch",
                    "The promoted artifact belongs to a different calibration attempt than the job.");
            }

            if (gcode.CalibrationProjectId.HasValue &&
                gcode.CalibrationProjectId != job.CalibrationProjectId)
            {
                return DispatchClaimResult.Fail(
                    "calibration_lineage_mismatch",
                    "The promoted artifact belongs to a different calibration project than the job.");
            }

            if (!string.IsNullOrWhiteSpace(gcode.SpecificationSha256) &&
                !string.Equals(gcode.SpecificationSha256, job.SpecificationSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "calibration_hash_mismatch",
                    "The promoted artifact's specification hash does not match the job's pinned specification hash.");
            }

            if (!string.IsNullOrWhiteSpace(gcode.MachineProfileSha256) &&
                !string.Equals(gcode.MachineProfileSha256, job.MachineProfileSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "calibration_hash_mismatch",
                    "The promoted artifact's machine profile hash does not match the job's pinned hash.");
            }

            if (!string.IsNullOrWhiteSpace(gcode.ProcessProfileSha256) &&
                !string.Equals(gcode.ProcessProfileSha256, job.ProcessProfileSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "calibration_hash_mismatch",
                    "The promoted artifact's process profile hash does not match the job's pinned hash.");
            }

            if (!string.IsNullOrWhiteSpace(gcode.FilamentProfileSha256) &&
                !string.Equals(gcode.FilamentProfileSha256, job.FilamentProfileSha256, StringComparison.OrdinalIgnoreCase))
            {
                return DispatchClaimResult.Fail(
                    "calibration_hash_mismatch",
                    "The promoted artifact's filament profile hash does not match the job's pinned hash.");
            }
        }

        // A blocked calibration job is never dispatchable.
        if (job.BlockedReasonCode.HasValue &&
            job.BlockedReasonCode is not (
                JobBlockedReasonCode.None or JobBlockedReasonCode.FilamentCheckFailed))
        {
            return DispatchClaimResult.Fail(
                "calibration_job_blocked",
                $"Calibration job is blocked: {job.BlockedReasonCode}.");
        }

        return null;
    }

    private static HashSet<string> BuildAdvertisedCapabilities(Printer printer)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (printer.HasHeatedBed)
        {
            _ = caps.Add("heated_bed");
        }

        if (printer.HasEnclosure)
        {
            _ = caps.Add("enclosure");
        }

        if (printer.HasHeatedChamber == true)
        {
            _ = caps.Add("heated_chamber");
        }

        if (printer.MultiMaterial || printer.HasMmu == true)
        {
            _ = caps.Add("multi_material");
            _ = caps.Add("mmu");
        }

        if (printer.SupportsAutoLeveling)
        {
            _ = caps.Add("auto_leveling");
        }

        if (printer.SupportsPressureAdvance == true)
        {
            _ = caps.Add("pressure_advance");
        }

        if (printer.SupportsFirmwareRetraction == true)
        {
            _ = caps.Add("firmware_retraction");
        }

        foreach (Toolhead toolhead in printer.Toolheads)
        {
            if (toolhead.NozzleIsHardened == true)
            {
                _ = caps.Add("hardened_nozzle");
            }

            if (toolhead.IsDirectDrive == true)
            {
                _ = caps.Add("direct_drive");
            }

            foreach (string material in toolhead.SupportedMaterials ?? [])
            {
                if (!string.IsNullOrWhiteSpace(material))
                {
                    _ = caps.Add(material.Trim());
                }
            }
        }

        return caps;
    }
}
