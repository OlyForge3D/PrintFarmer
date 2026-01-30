using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Gcode;

/// <summary>
/// Service for resolving printer model aliases.
/// Maps slicer-specific model names (e.g., "COREONEL", "Phrozen Arco")
/// to canonical PrinterModel IDs for consistent gcode file association.
/// </summary>
public interface IPrinterModelAliasService
{
    /// <summary>
    /// Resolves a slicer model name to its canonical PrinterModel ID.
    /// </summary>
    /// <param name="slicerModelName">The model name as it appears in gcode (e.g., "COREONEL")</param>
    /// <param name="slicerType">Optional slicer type (e.g., "PrusaSlicer", "OrcaSlicer").
    ///   If null, looks for alias that applies to all slicers.</param>
    /// <returns>PrinterModel ID if found, null if no matching alias exists.</returns>
    Task<Guid?> ResolveModelAliasAsync(string slicerModelName, string? slicerType = null);
}

/// <summary>
/// Default implementation of printer model alias resolution.
/// </summary>
public class PrinterModelAliasService(AppDbContext dbContext) : IPrinterModelAliasService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>
    /// Resolves a slicer model name to its canonical PrinterModel ID.
    /// Priority: Exact slicer-type match > Null slicer-type (applies to all)
    /// </summary>
    /// <param name="slicerModelName">The model name as it appears in gcode.</param>
    /// <param name="slicerType">Optional slicer type for slicer-specific matching.</param>
    public async Task<Guid?> ResolveModelAliasAsync(string slicerModelName, string? slicerType = null)
    {
        if (string.IsNullOrWhiteSpace(slicerModelName))
        {
            return null;
        }

        // Try exact match with slicer type first (case-insensitive)
        if (!string.IsNullOrEmpty(slicerType))
        {
            Guid exactMatch = await _dbContext.PrinterModelAliases
                .AsNoTracking()
                .Where(a => EF.Functions.Collate(a.SlicerModelName, "NOCASE") == slicerModelName && a.SlicerType == slicerType)
                .Select(a => a.PrinterModelId)
                .FirstOrDefaultAsync();

            if (exactMatch != Guid.Empty)
            {
                return exactMatch;
            }
        }

        // Fall back to slicer-agnostic alias (SlicerType is null, case-insensitive)
        Guid genericMatch = await _dbContext.PrinterModelAliases
            .AsNoTracking()
            .Where(a => EF.Functions.Collate(a.SlicerModelName, "NOCASE") == slicerModelName && a.SlicerType == null)
            .Select(a => a.PrinterModelId)
            .FirstOrDefaultAsync();

        return genericMatch != Guid.Empty ? genericMatch : null;
    }
}
