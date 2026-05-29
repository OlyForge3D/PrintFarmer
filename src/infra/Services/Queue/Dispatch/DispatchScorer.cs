using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.AutoTagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Scores and ranks candidate printers for a print job using weighted multi-factor analysis.
/// Hard requirements eliminate printers; soft factors produce a weighted average score.
/// </summary>
public class DispatchScorer(AppDbContext db, ILogger<DispatchScorer> logger) : IDispatchScorer
{
    // Factor weight constants
    private const double WeightMaterialMatch = 100;
    private const double WeightNozzleDiameter = 100;
    private const double WeightBuildVolume = 50;
    private const double WeightEnclosure = 80;
    private const double WeightNozzleHardness = 80;
    private const double WeightModelMatch = 60;
    private const double WeightQueueDepth = 30;
    private const double WeightPreferred = 40;
    private const double WeightColorMatch = 20;

    private const double NozzleDiameterTolerance = 0.01;

    public async Task<List<DispatchScore>> ScorePrintersForJobAsync(Guid jobId, CancellationToken ct = default)
    {
        PrintJob? job = await db.PrintJobs
            .Include(j => j.GcodeFile)
                .ThenInclude(g => g!.PrinterModel)
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
        {
            logger.LogWarning("Dispatch scorer: job {JobId} not found", jobId);
            return [];
        }

        // Pre-filter: get all enabled, non-maintenance printers with toolheads
        List<Printer> printers = await db.Printers
            .Include(p => p.Model)
                .ThenInclude(m => m!.SupportedFilamentTypes)
            .Include(p => p.Model)
                .ThenInclude(m => m!.Aliases)
            .Include(p => p.Toolheads)
                .ThenInclude(t => t.NozzleModel)
            .Include(p => p.DispatchState)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);

        // Batch-load queue depths for all printers in one query
        Dictionary<Guid, int> queueDepths = await db.PrintJobs
            .Where(j => j.AssignedPrinterId != null
                && j.Status != PrintJobStatus.Completed
                && j.Status != PrintJobStatus.Failed
                && j.Status != PrintJobStatus.Cancelled)
            .GroupBy(j => j.AssignedPrinterId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        // Resolve the required material's FilamentType for enclosure/abrasive checks
        FilamentType? requiredFilament = null;
        string? requiredMaterial = job.RequiredMaterialType ?? job.GcodeFile?.RequiredMaterial;
        if (!string.IsNullOrWhiteSpace(requiredMaterial))
        {
            requiredFilament = await db.FilamentTypes
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.Name == requiredMaterial && f.IsActive, ct);
        }

        // Pre-load cluster mate names for the required material (used for fallback matching)
        HashSet<string> clusterMateNames = [];
        if (!string.IsNullOrWhiteSpace(requiredMaterial))
        {
            clusterMateNames = await GetClusterMateNamesAsync(requiredMaterial, ct);
        }

        List<DispatchScore> results = [];

        foreach (Printer printer in printers)
        {
            DispatchScore score = ScorePrinter(job, printer, requiredFilament, queueDepths, clusterMateNames);
            results.Add(score);
        }

        // Sort: non-eliminated by score descending, then eliminated at the end
        results.Sort((a, b) =>
        {
            if (a.Eliminated != b.Eliminated)
            {
                return a.Eliminated ? 1 : -1;
            }

            return b.TotalScore.CompareTo(a.TotalScore);
        });

        logger.LogInformation(
            "Dispatch scorer: job {JobId} scored {Count} printers, {Eliminated} eliminated",
            jobId, results.Count, results.Count(r => r.Eliminated));

        return results;
    }

    private DispatchScore ScorePrinter(
        PrintJob job,
        Printer printer,
        FilamentType? requiredFilament,
        Dictionary<Guid, int> queueDepths,
        HashSet<string> clusterMateNames)
    {
        Dictionary<string, FactorScore> breakdown = [];
        List<string> eliminationReasons = [];
        bool eliminated = false;

        // Factor 9 (pre-filter): Printer Availability
        FactorScore availabilityScore = ScoreAvailability(printer);
        breakdown["Availability"] = availabilityScore;
        if (availabilityScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (availabilityScore.EliminationReason is not null)
            {
                eliminationReasons.Add(availabilityScore.EliminationReason);
            }
        }

        // Factor 1: Material Match
        string? requiredMaterial = job.RequiredMaterialType ?? job.GcodeFile?.RequiredMaterial;
        FactorScore materialScore = ScoreMaterialMatch(printer, requiredMaterial, clusterMateNames);
        breakdown["MaterialMatch"] = materialScore;
        if (materialScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (materialScore.EliminationReason is not null)
            {
                eliminationReasons.Add(materialScore.EliminationReason);
            }
        }

        // Factor 2: Nozzle Diameter Match
        decimal? requiredNozzle = job.RequiredNozzleDiameter ?? (decimal?)job.GcodeFile?.RequiredNozzleDiameter;
        FactorScore nozzleScore = ScoreNozzleDiameter(printer, requiredNozzle);
        breakdown["NozzleDiameter"] = nozzleScore;
        if (nozzleScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (nozzleScore.EliminationReason is not null)
            {
                eliminationReasons.Add(nozzleScore.EliminationReason);
            }
        }

        // Factor 3: Build Volume Fit
        FactorScore volumeScore = ScoreBuildVolume(printer, job.GcodeFile);
        breakdown["BuildVolume"] = volumeScore;

        // Factor 4: Enclosure Requirement
        FactorScore enclosureScore = ScoreEnclosure(printer, requiredFilament);
        breakdown["Enclosure"] = enclosureScore;
        if (enclosureScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (enclosureScore.EliminationReason is not null)
            {
                eliminationReasons.Add(enclosureScore.EliminationReason);
            }
        }

        // Factor 5: Nozzle Hardness
        FactorScore hardnessScore = ScoreNozzleHardness(printer, requiredFilament);
        breakdown["NozzleHardness"] = hardnessScore;
        if (hardnessScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (hardnessScore.EliminationReason is not null)
            {
                eliminationReasons.Add(hardnessScore.EliminationReason);
            }
        }

        // Factor 6: Printer Model Match
        // Updated: Uses PrinterGroup hard-elimination when GcodeFile has a group assigned.
        // If gcode has a PrinterGroupId, the candidate MUST be in that group or be eliminated.
        FactorScore modelScore = ScoreModelMatch(printer, job.GcodeFile);
        breakdown["ModelMatch"] = modelScore;

        // Factor 10: Printer Group (hard elimination)
        FactorScore groupScore = ScorePrinterGroup(printer, job.GcodeFile);
        breakdown["PrinterGroup"] = groupScore;
        if (groupScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (groupScore.EliminationReason is not null)
            {
                eliminationReasons.Add(groupScore.EliminationReason);
            }
        }

        // Factor 7: Queue Depth
        int depth = queueDepths.GetValueOrDefault(printer.Id, 0);
        FactorScore queueScore = ScoreQueueDepth(depth);
        breakdown["QueueDepth"] = queueScore;

        // Factor 8: Preferred Printer
        FactorScore preferredScore = ScorePreferred(printer.Id, job.PreferredPrinterIds, job.ExcludedPrinterIds);
        breakdown["Preferred"] = preferredScore;
        if (preferredScore is { IsHardRequirement: true, Score: 0 })
        {
            eliminated = true;
            if (preferredScore.EliminationReason is not null)
            {
                eliminationReasons.Add(preferredScore.EliminationReason);
            }
        }

        // Factor 11: Color Match (soft preference)
        FactorScore colorScore = ScoreColorMatch(printer, job);
        breakdown["ColorMatch"] = colorScore;

        // Calculate weighted average: Σ(score × weight) / Σ(weights)
        double totalScore = 0;
        if (!eliminated)
        {
            double weightedSum = breakdown.Values.Sum(f => f.WeightedScore);
            double totalWeight = breakdown.Values.Sum(f => f.Weight);
            totalScore = totalWeight > 0 ? weightedSum / totalWeight : 0;
        }

        return new DispatchScore(
            printer.Id,
            printer.Name,
            Math.Round(totalScore, 2),
            breakdown,
            eliminated,
            eliminationReasons);
    }

    private static FactorScore ScoreAvailability(Printer printer)
    {
        List<string> issues = [];
        if (!printer.IsAvailable)
        {
            issues.Add("not available");
        }

        if (printer.InMaintenance)
        {
            issues.Add("in maintenance");
        }

        if (!printer.IsEnabled)
        {
            issues.Add("disabled");
        }

        if ((printer.DispatchState?.AutoDispatchState ?? AutoDispatchState.None) == AutoDispatchState.PendingReady)
        {
            issues.Add("waiting for bed clear confirmation");
        }

        if (issues.Count > 0)
        {
            string reason = $"Printer is {string.Join(", ", issues)}";
            return new FactorScore("Availability", 0, 0, 0, true, reason);
        }

        // Availability is a pre-filter, contributes zero weight to the score
        return new FactorScore("Availability", 100, 0, 0, true);
    }

    private static FactorScore ScoreMaterialMatch(Printer printer, string? requiredMaterial, HashSet<string> clusterMateNames)
    {
        if (string.IsNullOrWhiteSpace(requiredMaterial))
        {
            return new FactorScore("MaterialMatch", 70, WeightMaterialMatch, 70 * WeightMaterialMatch, false);
        }

        // Check if printer's currently loaded material matches exactly
        if (!string.IsNullOrWhiteSpace(printer.CurrentMaterial)
            && string.Equals(printer.CurrentMaterial, requiredMaterial, StringComparison.OrdinalIgnoreCase))
        {
            return new FactorScore("MaterialMatch", 100, WeightMaterialMatch, 100 * WeightMaterialMatch, true);
        }

        // Check if printer's loaded material is a cluster mate (equivalent material)
        if (!string.IsNullOrWhiteSpace(printer.CurrentMaterial)
            && clusterMateNames.Count > 0
            && clusterMateNames.Contains(printer.CurrentMaterial))
        {
            return new FactorScore("MaterialMatch", 85, WeightMaterialMatch, 85 * WeightMaterialMatch, true);
        }

        // Check toolhead supported materials
        bool anyToolheadSupports = printer.Toolheads.Any(t =>
            t.SupportedMaterials?.Any(m =>
                string.Equals(m, requiredMaterial, StringComparison.OrdinalIgnoreCase)) == true);

        if (anyToolheadSupports)
        {
            // Printer can handle the material but doesn't have it loaded
            return new FactorScore("MaterialMatch", 50, WeightMaterialMatch, 50 * WeightMaterialMatch, true);
        }

        // Check toolhead supported materials via cluster equivalence
        if (clusterMateNames.Count > 0)
        {
            bool anyToolheadSupportsCluster = printer.Toolheads.Any(t =>
                t.SupportedMaterials?.Any(m => clusterMateNames.Contains(m)) == true);

            if (anyToolheadSupportsCluster)
            {
                return new FactorScore("MaterialMatch", 45, WeightMaterialMatch, 45 * WeightMaterialMatch, true);
            }
        }

        // Check printer model's supported filament types
        bool modelSupports = printer.Model?.SupportedFilamentTypes.Any(f =>
            string.Equals(f.Name, requiredMaterial, StringComparison.OrdinalIgnoreCase)) == true;

        if (modelSupports)
        {
            return new FactorScore("MaterialMatch", 40, WeightMaterialMatch, 40 * WeightMaterialMatch, true);
        }

        // Check printer model's supported filament types via cluster equivalence
        if (clusterMateNames.Count > 0)
        {
            bool modelSupportsCluster = printer.Model?.SupportedFilamentTypes.Any(f =>
                clusterMateNames.Contains(f.Name)) == true;

            if (modelSupportsCluster)
            {
                return new FactorScore("MaterialMatch", 35, WeightMaterialMatch, 35 * WeightMaterialMatch, true);
            }
        }

        // No data about supported materials — don't eliminate, score low
        if (printer.Toolheads.All(t => t.SupportedMaterials is null or { Length: 0 })
            && (printer.Model?.SupportedFilamentTypes.Count ?? 0) == 0)
        {
            return new FactorScore("MaterialMatch", 30, WeightMaterialMatch, 30 * WeightMaterialMatch, true);
        }

        // Has material data but required material isn't in it
        return new FactorScore("MaterialMatch", 0, WeightMaterialMatch, 0, true,
            $"Printer does not support material '{requiredMaterial}'");
    }

    private static FactorScore ScoreNozzleDiameter(Printer printer, decimal? requiredDiameter)
    {
        if (!requiredDiameter.HasValue)
        {
            return new FactorScore("NozzleDiameter", 70, WeightNozzleDiameter, 70 * WeightNozzleDiameter, false);
        }

        double required = (double)requiredDiameter.Value;

        // Check nozzle models first (more precise)
        foreach (Toolhead toolhead in printer.Toolheads)
        {
            if (toolhead.NozzleModel is not null)
            {
                if (Math.Abs(toolhead.NozzleModel.Diameter - required) <= NozzleDiameterTolerance)
                {
                    return new FactorScore("NozzleDiameter", 100, WeightNozzleDiameter, 100 * WeightNozzleDiameter, true);
                }
            }
        }

        // No nozzle model data — no data, score neutral
        if (printer.Toolheads.All(t => t.NozzleModel is null))
        {
            return new FactorScore("NozzleDiameter", 50, WeightNozzleDiameter, 50 * WeightNozzleDiameter, true);
        }

        // Has nozzle data but none match
        return new FactorScore("NozzleDiameter", 0, WeightNozzleDiameter, 0, true,
            $"No toolhead has nozzle diameter {required:F2}mm (±{NozzleDiameterTolerance}mm)");
    }

    private static FactorScore ScoreBuildVolume(Printer printer, GcodeFile? gcode)
    {
        // Build volume is a soft factor — no gcode size data means neutral score
        if (gcode is null)
        {
            return new FactorScore("BuildVolume", 70, WeightBuildVolume, 70 * WeightBuildVolume, false);
        }

        // We don't have per-gcode bounding box data yet, but we can compare against the
        // printer model that the gcode was sliced for vs this printer's build volume
        double? printerX = printer.MaxBuildVolumeX ?? printer.Model?.MaxX;
        double? printerY = printer.MaxBuildVolumeY ?? printer.Model?.MaxY;
        double? printerZ = printer.MaxBuildVolumeZ ?? printer.Model?.MaxZ;

        if (printerX is null || printerY is null || printerZ is null)
        {
            return new FactorScore("BuildVolume", 60, WeightBuildVolume, 60 * WeightBuildVolume, false);
        }

        // If the gcode was sliced for a specific model, and we know that model's volume,
        // check that this printer's volume is >= the target model's volume
        if (gcode.PrinterModel is not null)
        {
            double? gcodeX = gcode.PrinterModel.MaxX;
            double? gcodeY = gcode.PrinterModel.MaxY;
            double? gcodeZ = gcode.PrinterModel.MaxZ;

            if (gcodeX.HasValue && gcodeY.HasValue && gcodeZ.HasValue)
            {
                if (printerX >= gcodeX && printerY >= gcodeY && printerZ >= gcodeZ)
                {
                    return new FactorScore("BuildVolume", 100, WeightBuildVolume, 100 * WeightBuildVolume, false);
                }

                // Printer build volume is smaller — risky but not eliminated
                return new FactorScore("BuildVolume", 20, WeightBuildVolume, 20 * WeightBuildVolume, false);
            }
        }

        return new FactorScore("BuildVolume", 70, WeightBuildVolume, 70 * WeightBuildVolume, false);
    }

    private static FactorScore ScoreEnclosure(Printer printer, FilamentType? filament)
    {
        if (filament is null || !filament.NeedsEnclosure)
        {
            return new FactorScore("Enclosure", 100, WeightEnclosure, 100 * WeightEnclosure, false);
        }

        // Material needs enclosure — this is a hard requirement
        bool hasEnclosure = printer.HasEnclosure || (printer.Model?.HasEnclosure ?? false);
        if (hasEnclosure)
        {
            return new FactorScore("Enclosure", 100, WeightEnclosure, 100 * WeightEnclosure, true);
        }

        return new FactorScore("Enclosure", 0, WeightEnclosure, 0, true,
            $"Material '{filament.Name}' requires an enclosure but printer has none");
    }

    private static FactorScore ScoreNozzleHardness(Printer printer, FilamentType? filament)
    {
        if (filament is null || !filament.IsAbrasive)
        {
            return new FactorScore("NozzleHardness", 100, WeightNozzleHardness, 100 * WeightNozzleHardness, false);
        }

        // Material is abrasive — hardened nozzle is a hard requirement
        bool hasHardenedNozzle = printer.Toolheads.Any(t =>
            t.NozzleModel is not null && t.NozzleModel.IsHardened);

        if (hasHardenedNozzle)
        {
            return new FactorScore("NozzleHardness", 100, WeightNozzleHardness, 100 * WeightNozzleHardness, true);
        }

        // No nozzle data — can't confirm hardened, treat as unknown (don't eliminate)
        if (printer.Toolheads.All(t => t.NozzleModel is null))
        {
            return new FactorScore("NozzleHardness", 30, WeightNozzleHardness, 30 * WeightNozzleHardness, true);
        }

        return new FactorScore("NozzleHardness", 0, WeightNozzleHardness, 0, true,
            $"Material '{filament.Name}' is abrasive but no hardened nozzle found");
    }

    private static FactorScore ScoreModelMatch(Printer printer, GcodeFile? gcode)
    {
        if (gcode is null || gcode.PrinterModelId is null)
        {
            // No data — neutral score
            return new FactorScore("ModelMatch", 70, WeightModelMatch, 70 * WeightModelMatch, false);
        }

        // Exact model match
        if (printer.ModelId == gcode.PrinterModelId)
        {
            return new FactorScore("ModelMatch", 100, WeightModelMatch, 100 * WeightModelMatch, false);
        }

        // Same manufacturer
        if (printer.Model is not null && gcode.PrinterModel is not null
            && printer.Model.ManufacturerId == gcode.PrinterModel.ManufacturerId)
        {
            return new FactorScore("ModelMatch", 50, WeightModelMatch, 50 * WeightModelMatch, false);
        }

        // Different manufacturer
        return new FactorScore("ModelMatch", 30, WeightModelMatch, 30 * WeightModelMatch, false);
    }

    private static FactorScore ScoreQueueDepth(int depth)
    {
        double score = depth switch
        {
            0 => 100,
            <= 2 => 70,
            <= 5 => 40,
            _ => 10,
        };
        return new FactorScore("QueueDepth", score, WeightQueueDepth, score * WeightQueueDepth, false);
    }

    private static FactorScore ScorePreferred(Guid printerId, Guid[]? preferred, Guid[]? excluded)
    {
        if (excluded is not null && excluded.Contains(printerId))
        {
            return new FactorScore("Preferred", 0, WeightPreferred, 0, true,
                "Printer is in the excluded list");
        }

        if (preferred is not null && preferred.Length > 0)
        {
            if (preferred.Contains(printerId))
            {
                return new FactorScore("Preferred", 100, WeightPreferred, 100 * WeightPreferred, false);
            }

            // Not in preferred list — lower score but don't eliminate
            return new FactorScore("Preferred", 30, WeightPreferred, 30 * WeightPreferred, false);
        }

        // No preference set — neutral
        return new FactorScore("Preferred", 70, WeightPreferred, 70 * WeightPreferred, false);
    }

    /// <summary>
    /// Hard elimination: if the G-code file specifies a PrinterGroupId, the candidate
    /// printer MUST be in that group. If no group is specified, all printers pass (backward compat).
    /// Weight is 0 — this is a gate, not a scoring factor.
    /// </summary>
    private static FactorScore ScorePrinterGroup(Printer printer, GcodeFile? gcode)
    {
        if (gcode?.PrinterGroupId is null)
        {
            // No group constraint — backward compatible, all printers pass
            return new FactorScore("PrinterGroup", 100, 0, 0, false);
        }

        if (printer.PrinterGroupId == gcode.PrinterGroupId)
        {
            return new FactorScore("PrinterGroup", 100, 0, 0, true);
        }

        // Printer is not in the required group — hard eliminate
        return new FactorScore("PrinterGroup", 0, 0, 0, true,
            $"G-code requires printer group '{gcode.PrinterGroupId}' but printer is in group '{printer.PrinterGroupId?.ToString() ?? "none"}'");
    }

    private FactorScore ScoreColorMatch(Printer printer, PrintJob job)
    {
        if (string.IsNullOrWhiteSpace(job.FilamentColor))
        {
            // Job doesn't specify a color — not a factor
            return new FactorScore("ColorMatch", 50, WeightColorMatch, 50 * WeightColorMatch, false);
        }

        // Get printer's loaded filament color from primary toolhead
        string? printerColor = printer.Toolheads
            .Where(t => t.IsPrimary)
            .Select(t => t.CurrentFilamentColor)
            .FirstOrDefault()
            ?? printer.Toolheads
                .Select(t => t.CurrentFilamentColor)
                .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        if (string.IsNullOrWhiteSpace(printerColor))
        {
            // Printer has no color data — neutral
            logger.LogDebug("Dispatch color: printer {PrinterName} has no loaded color data", printer.Name);
            return new FactorScore("ColorMatch", 50, WeightColorMatch, 50 * WeightColorMatch, false);
        }

        // Exact hex match (case-insensitive, normalize # prefix)
        string jobHex = job.FilamentColor.Trim().TrimStart('#').ToUpperInvariant();
        string printerHex = printerColor.Trim().TrimStart('#').ToUpperInvariant();

        if (string.Equals(jobHex, printerHex, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Dispatch color: exact hex match for printer {PrinterName} (#{Hex})",
                printer.Name, jobHex);
            return new FactorScore("ColorMatch", 100, WeightColorMatch, 100 * WeightColorMatch, false);
        }

        // Compare color families
        (string Name, string Hex)? jobFamily = AutoTagService.HexToColorFamily(job.FilamentColor);
        (string Name, string Hex)? printerFamily = AutoTagService.HexToColorFamily(printerColor);

        if (jobFamily is null || printerFamily is null)
        {
            // Can't parse color — neutral
            return new FactorScore("ColorMatch", 50, WeightColorMatch, 50 * WeightColorMatch, false);
        }

        if (string.Equals(jobFamily.Value.Name, printerFamily.Value.Name, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Dispatch color: same family '{Family}' for printer {PrinterName} (job #{JobHex} vs printer #{PrinterHex})",
                jobFamily.Value.Name, printer.Name, jobHex, printerHex);
            return new FactorScore("ColorMatch", 80, WeightColorMatch, 80 * WeightColorMatch, false);
        }

        // Different color family — slight penalty, don't eliminate
        logger.LogDebug(
            "Dispatch color: family mismatch for printer {PrinterName} (job {JobFamily} vs printer {PrinterFamily})",
            printer.Name, jobFamily.Value.Name, printerFamily.Value.Name);
        return new FactorScore("ColorMatch", 20, WeightColorMatch, 20 * WeightColorMatch, false);
    }

    /// <summary>
    /// Returns the names of all filament types that share a material cluster with the given name.
    /// Used to score cluster-equivalent materials as a fallback when no exact match exists.
    /// </summary>
    private async Task<HashSet<string>> GetClusterMateNamesAsync(string filamentTypeName, CancellationToken ct)
    {
        List<Guid> clusterIds = await db.MaterialClusterMembers
            .Include(m => m.FilamentType)
            .Where(m => m.FilamentType.Name == filamentTypeName)
            .Select(m => m.ClusterId)
            .Distinct()
            .ToListAsync(ct);

        if (clusterIds.Count == 0)
        {
            return [];
        }

        List<string> names = await db.MaterialClusterMembers
            .Include(m => m.FilamentType)
            .Where(m => clusterIds.Contains(m.ClusterId))
            .Select(m => m.FilamentType.Name)
            .Distinct()
            .ToListAsync(ct);

        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }
}
