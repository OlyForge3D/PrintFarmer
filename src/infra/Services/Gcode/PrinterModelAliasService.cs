using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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

    /// <summary>
    /// Ensures an exact slicer alias maps to the requested catalog model.
    /// </summary>
    /// <param name="printerModelId">Target catalog printer model.</param>
    /// <param name="slicerModelName">Exact slicer-native model name.</param>
    /// <param name="slicerType">Slicer engine owning the alias.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureModelAliasAsync(
        Guid printerModelId,
        string slicerModelName,
        string slicerType,
        CancellationToken ct = default);
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

    /// <inheritdoc />
    public async Task EnsureModelAliasAsync(
        Guid printerModelId,
        string slicerModelName,
        string slicerType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slicerModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(slicerType);

        List<PrinterModelAlias> existing = await _dbContext.PrinterModelAliases
            .Where(alias =>
                alias.SlicerModelName == slicerModelName &&
                alias.SlicerType == slicerType)
            .ToListAsync(ct);
        if (existing.Any(alias => alias.PrinterModelId != printerModelId))
        {
            throw new InvalidOperationException(
                $"Slicer alias '{slicerModelName}' is already mapped to another printer model.");
        }

        if (existing.Count > 0)
        {
            return;
        }

        _ = _dbContext.PrinterModelAliases.Add(new Domain.PrinterModelAlias
        {
            Id = Guid.NewGuid(),
            PrinterModelId = printerModelId,
            SlicerModelName = slicerModelName,
            SlicerType = slicerType,
            CreatedAt = DateTime.UtcNow
        });
        _ = await _dbContext.SaveChangesAsync(ct);
    }
}
