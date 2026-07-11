using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Default implementation of <see cref="IPrinterToolheadSwapValidator"/>. Reads active and
/// assigned jobs from the print queue, resolves the requested toolhead's expected material,
/// and compares against the scanned Spoolman spool's material.
/// </summary>
public class PrinterToolheadSwapValidator(
    AppDbContext db,
    ISpoolmanService spoolman,
    ILogger<PrinterToolheadSwapValidator> logger) : IPrinterToolheadSwapValidator
{
    private static readonly PrintJobStatus[] ActiveOrPendingStatuses = new[]
    {
        PrintJobStatus.Starting,
        PrintJobStatus.Printing,
        PrintJobStatus.Paused,
        PrintJobStatus.Assigned,
        PrintJobStatus.Queued,
    };

    /// <inheritdoc />
    public async Task<SwapValidationResultDto?> ValidateAsync(
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
            return null;
        }

        if (toolheadIndex < 0)
        {
            return null;
        }

        Toolhead? toolhead = printer.Toolheads?.FirstOrDefault(t => t.Index == toolheadIndex);
        if (toolhead is null)
        {
            // Allow validation for T0 on legacy single-tool printers that have not seeded a
            // Toolhead row yet — the guided swap flow should still work.
            if (toolheadIndex != 0)
            {
                return null;
            }
        }

        // Translate the caller-supplied toolhead.Index into the 0-based G-code tool index
        // used by RequiredMaterialsPerTool. For virtual MMU gates (Index 1..N) this is
        // Index-1; for physical toolheads (single-tool, U1 lanes, toolchangers) this is
        // Index. When we synthesise a legacy T0 without a Toolhead row, the caller's
        // toolheadIndex (0) is already the correct G-code index.
        int? gcodeToolIndex = toolhead is null
            ? toolheadIndex
            : ToolheadIndexMapper.ToGcodeToolIndex(toolhead);

        if (gcodeToolIndex is null)
        {
            // MmuGate stored at Index=0 has no G-code tool mapping (the physical hotend
            // shared by an MMU is not itself a filament source). Treat as 404 so clients
            // do not silently accept unmapped scans.
            return null;
        }

        SpoolmanSpoolDto? spool = null;
        try
        {
            spool = await spoolman.GetSpoolByIdAsync(spoolId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Swap validation for printer {PrinterId} toolhead T{ToolheadIndex}: Spoolman lookup for spool {SpoolId} failed",
                printerId,
                toolheadIndex,
                spoolId);
        }

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
        foreach (PrintJob job in candidateJobs)
        {
            string? material = ExtractExpectedMaterial(job, gcodeToolIndex.Value);
            if (material is not null)
            {
                expected = material;
                break;
            }
        }

        if (spool is null)
        {
            return new SwapValidationResultDto(
                Ok: false,
                Expected: expected,
                Scanned: null,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: "Scanned spool not found in Spoolman.");
        }

        if (expected is null)
        {
            return new SwapValidationResultDto(
                Ok: true,
                Expected: null,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: null);
        }

        bool matches = !string.IsNullOrWhiteSpace(scanned)
            && string.Equals(scanned.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

        if (matches)
        {
            return new SwapValidationResultDto(
                Ok: true,
                Expected: expected,
                Scanned: scanned,
                AffectedJobs: Array.Empty<SwapValidationAffectedJobDto>(),
                Reason: null);
        }

        List<SwapValidationAffectedJobDto> affected = new(capacity: candidateJobs.Count);
        foreach (PrintJob job in candidateJobs)
        {
            string? material = ExtractExpectedMaterial(job, gcodeToolIndex.Value);
            if (material is null)
            {
                continue;
            }

            if (!string.Equals(scanned?.Trim(), material.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                affected.Add(new SwapValidationAffectedJobDto(
                    JobId: job.Id,
                    Name: job.Name,
                    Status: job.Status,
                    Tool: gcodeToolIndex.Value,
                    ExpectedMaterial: material));
            }
        }

        return new SwapValidationResultDto(
            Ok: false,
            Expected: expected,
            Scanned: scanned,
            AffectedJobs: affected,
            Reason: $"Scanned material '{scanned ?? "(unknown)"}' does not match expected '{expected}'.");
    }

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
