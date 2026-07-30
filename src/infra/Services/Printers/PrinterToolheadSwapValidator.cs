using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Default implementation of <see cref="IPrinterToolheadSwapValidator"/>. Reads active and
/// assigned jobs from the print queue, resolves the requested toolhead's expected material,
/// and compares against the scanned Spoolman spool's material.
/// </summary>
/// <remarks>
/// This validator performs NO writes. When the requested lane has no materialized
/// <see cref="Toolhead"/> row yet it synthesizes the lane's semantics from the printer's
/// MMU capability + index mapping so a valid-but-unmaterialized gate is still validated
/// (never blindly bound); genuinely invalid / out-of-range lanes are surfaced via
/// <see cref="SwapValidationOutcome"/> so the controller returns 404/400 with no write
/// (GitHub issue OlyForge3D/PrintFarmer#710, B2/B3).
/// </remarks>
public class PrinterToolheadSwapValidator(
    AppDbContext db,
    IFilamentCoverageSpoolResolver spoolResolver) : IPrinterToolheadSwapValidator
{
    /// <summary>
    /// Hard upper bound on a toolhead / MMU-gate index. Mirrors
    /// <c>PrintersService.MaxToolheadIndex</c> so validation and binding agree on range.
    /// </summary>
    internal const int MaxToolheadIndex = 16;

    private static readonly PrintJobStatus[] ActiveOrPendingStatuses = new[]
    {
        PrintJobStatus.Starting,
        PrintJobStatus.Printing,
        PrintJobStatus.Paused,
        PrintJobStatus.Assigned,
        PrintJobStatus.Queued,
    };

    /// <inheritdoc />
    public async Task<SwapValidationResult> ValidateAsync(
        Guid printerId,
        int toolheadIndex,
        int spoolId,
        CancellationToken ct)
    {
        Printer? printer = await db.Printers
            .Include(p => p.Toolheads)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct)
            .ConfigureAwait(false);

        if (printer is null)
        {
            return new SwapValidationResult(SwapValidationOutcome.PrinterNotFound, null);
        }

        // Structural range check first: a negative or oversized index is never a valid lane.
        if (toolheadIndex < 0 || toolheadIndex > MaxToolheadIndex)
        {
            return new SwapValidationResult(SwapValidationOutcome.ToolheadOutOfRange, null);
        }

        // Resolve the 0-based G-code tool index for the requested lane, synthesizing the
        // descriptor when the Toolhead row is not materialized yet (B2/B3). A null result
        // means the lane is not a valid filament source → 404, no write.
        int? gcodeToolIndex = ResolveGcodeToolIndex(printer, toolheadIndex);
        if (gcodeToolIndex is null)
        {
            return new SwapValidationResult(SwapValidationOutcome.ToolheadNotFound, null);
        }

        FilamentCoverageSpoolSnapshot spoolSnapshot = await spoolResolver
            .ResolveSpoolAsync(printer, spoolId, ct)
            .ConfigureAwait(false);
        SpoolmanSpoolDto? spool = spoolSnapshot.Spool;

        string? scanned = spool?.Material;

        List<PrintJob> candidateJobs = await db.PrintJobs
            .Include(j => j.GcodeFile)
            .AsNoTracking()
            .Where(j => j.AssignedPrinterId == printerId && ActiveOrPendingStatuses.Contains(j.Status))
            .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Paused ? 0 : 1)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        string? expected = null;
        bool anyUsesToolButUnknown = false;
        foreach (PrintJob job in candidateJobs)
        {
            string? material = ExtractExpectedMaterial(job, gcodeToolIndex.Value);
            if (material is not null)
            {
                expected ??= material;
                continue;
            }

            if (JobUsesGcodeTool(job, gcodeToolIndex.Value))
            {
                anyUsesToolButUnknown = true;
            }
        }

        // Unresolved / nonexistent Spoolman spool → UNKNOWN, never mismatch. Guided binding
        // must not proceed or override on an unknown result (B7).
        if (spool is null)
        {
            string reason = spoolSnapshot.ErrorReason is null
                ? "Scanned spool could not be resolved from the printer's spool source."
                : $"Scanned spool could not be resolved from the printer's spool source ({spoolSnapshot.ErrorReason}).";
            return Validated(new SwapValidationResultDto(
                Status: SwapValidationStatus.Unknown,
                Expected: expected,
                Scanned: null,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: reason));
        }

        // Any relevant job that uses this tool without a resolvable material makes the
        // validation unknown, even if another relevant job has a known requirement.
        if (anyUsesToolButUnknown)
        {
            return Validated(new SwapValidationResultDto(
                Status: SwapValidationStatus.Unknown,
                Expected: expected,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: "A queued or active job uses this tool but its required material is unknown."));
        }

        // No relevant job uses this lane, so there is no requirement to satisfy.
        if (expected is null)
        {
            return Validated(new SwapValidationResultDto(
                Status: SwapValidationStatus.Ok,
                Expected: null,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: null));
        }

        // A requirement exists but the scanned spool carries no material metadata → UNKNOWN
        // (cannot compare); not a mismatch (B7).
        if (string.IsNullOrWhiteSpace(scanned))
        {
            return Validated(new SwapValidationResultDto(
                Status: SwapValidationStatus.Unknown,
                Expected: expected,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: "Scanned spool has no material metadata to validate."));
        }

        bool matches = string.Equals(scanned.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            return Validated(new SwapValidationResultDto(
                Status: SwapValidationStatus.Ok,
                Expected: expected,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: null));
        }

        List<SwapValidationAffectedJobDto> affected = new(capacity: candidateJobs.Count);
        foreach (PrintJob job in candidateJobs)
        {
            string? material = ExtractExpectedMaterial(job, gcodeToolIndex.Value);
            if (material is null)
            {
                continue;
            }

            if (!string.Equals(scanned.Trim(), material.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                affected.Add(new SwapValidationAffectedJobDto(
                    JobId: job.Id,
                    Name: job.Name,
                    Status: job.Status,
                    Tool: gcodeToolIndex.Value,
                    ExpectedMaterial: material));
            }
        }

        return Validated(new SwapValidationResultDto(
            Status: SwapValidationStatus.Mismatch,
            Expected: expected,
            Scanned: scanned,
            AffectedJobs: affected,
            Reason: $"Scanned material '{scanned}' does not match expected '{expected}'."));
    }

    /// <summary>
    /// Determines whether <paramref name="job"/> will actually use the 0-based G-code tool
    /// <paramref name="gcodeToolIndex"/>, independent of whether its material is known. This is
    /// what lets the validator tell a genuinely unused lane (→ ok) apart from a lane that a
    /// relevant job uses but whose material is missing/blank/unresolved (→ unknown) — issue
    /// #710, C2.
    /// <para>
    /// Signals, in order of authority:
    /// <list type="number">
    /// <item>An explicit per-tool requirement slot for the index (even with blank material) —
    /// the slicer emitted this extruder.</item>
    /// <item>The linked G-code file's per-extruder metadata: a non-zero filament weight, or a
    /// present type slot, at the index means the extruder printed.</item>
    /// </list>
    /// Persisted per-tool arrays are sparse (blank slots were dropped on projection), so the
    /// G-code file is consulted as the authoritative fallback for existing jobs.
    /// </para>
    /// </summary>
    internal static bool JobUsesGcodeTool(PrintJob job, int gcodeToolIndex)
    {
        if (gcodeToolIndex < 0)
        {
            return false;
        }

        IReadOnlyList<PrintJobToolMaterialRequirement>? perTool = job.RequiredMaterialsPerTool;
        if (perTool is not null && perTool.Any(r => r.Tool == gcodeToolIndex))
        {
            return true;
        }

        GcodeFile? gcode = job.GcodeFile;
        if (gcode is not null)
        {
            double[]? weights = TryParseJsonArray<double>(gcode.FilamentPerExtruderWeightG);
            if (weights is not null && gcodeToolIndex < weights.Length && weights[gcodeToolIndex] > 0)
            {
                return true;
            }

            string[]? types = TryParseJsonArray<string>(gcode.FilamentPerExtruderType);
            if (types is not null && gcodeToolIndex < types.Length
                && !string.IsNullOrWhiteSpace(types[gcodeToolIndex]))
            {
                return true;
            }
        }

        return false;
    }

    private static T[]? TryParseJsonArray<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T[]>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static SwapValidationResult Validated(SwapValidationResultDto dto) =>
        new(SwapValidationOutcome.Validated, dto);

    /// <summary>
    /// Resolves the 0-based G-code tool index for a caller-supplied toolhead index, using the
    /// materialized <see cref="Toolhead"/> row when present and otherwise synthesizing the
    /// lane semantics from the printer's MMU capability. Returns <c>null</c> when the lane is
    /// not a valid filament source (the caller maps this to 404, no write).
    /// </summary>
    internal static int? ResolveGcodeToolIndex(Printer printer, int toolheadIndex)
    {
        Toolhead? toolhead = printer.Toolheads?.FirstOrDefault(t => t.Index == toolheadIndex);
        if (toolhead is not null)
        {
            return ToolheadIndexMapper.ToFilamentSourceGcodeToolIndex(
                toolhead,
                printer.Toolheads ?? []);
        }

        // No materialized row. Synthesize a descriptor from the printer's capability so a
        // valid-but-unmaterialized lane is still validated instead of blindly bound.
        if (toolheadIndex == 0)
        {
            // Index 0 is a filament source only on non-MMU printers. On an MMU printer it is the
            // shared physical hotend and gate 1 owns G-code T0.
            return IsMmuCapable(printer) ? null : 0;
        }

        // A gate index (> 0) is only meaningful on an MMU / multi-material printer, where the
        // gate at Index N maps to G-code tool N-1.
        if (IsMmuCapable(printer))
        {
            return toolheadIndex - 1;
        }

        // Non-MMU printer requesting a non-existent lane → not a valid filament source.
        return null;
    }

    /// <summary>
    /// True when the printer can host virtual MMU gates (either flagged multi-material / MMU
    /// or already carrying at least one materialized gate row).
    /// </summary>
    internal static bool IsMmuCapable(Printer printer) =>
        printer.MultiMaterial
        || printer.HasMmu == true
        || (printer.Toolheads?.Any(t => t.ToolheadType == ToolheadType.MmuGate) ?? false);

    /// <summary>
    /// Extracts the expected material for the 0-based G-code tool index
    /// <paramref name="gcodeToolIndex"/> from a job.
    /// <para>
    /// Per-tool requirements are AUTHORITATIVE when present: if <paramref name="gcodeToolIndex"/>
    /// has no entry in <see cref="PrintJob.RequiredMaterialsPerTool"/>, the answer is
    /// "no requirement" — the caller must NOT fall back to the legacy scalar. The legacy
    /// <see cref="PrintJob.RequiredMaterialType"/> is consulted only when per-tool data is
    /// absent entirely (single-material / pre-#710 jobs), and even then only for T0.
    /// </para>
    /// </summary>
    internal static string? ExtractExpectedMaterial(PrintJob job, int gcodeToolIndex)
    {
        IReadOnlyList<PrintJobToolMaterialRequirement>? perTool = job.RequiredMaterialsPerTool;
        if (perTool is not null)
        {
            PrintJobToolMaterialRequirement? match = perTool.FirstOrDefault(r => r.Tool == gcodeToolIndex);
            return match is not null && !string.IsNullOrWhiteSpace(match.MaterialType)
                ? match.MaterialType
                : null;
        }

        if (gcodeToolIndex == 0 && !string.IsNullOrWhiteSpace(job.RequiredMaterialType))
        {
            return job.RequiredMaterialType;
        }

        return null;
    }
}
