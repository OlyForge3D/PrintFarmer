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

    /// <summary>
    /// Removes the exact slicer alias mapping the given model name (for the given slicer engine)
    /// to the specified catalog model. Idempotent: removing an absent alias is a no-op. Aliases
    /// mapped to a different catalog model, or generic (slicer-agnostic) aliases, are left untouched.
    /// </summary>
    /// <param name="printerModelId">Catalog printer model the alias must currently map to.</param>
    /// <param name="slicerModelName">Exact slicer-native model name.</param>
    /// <param name="slicerType">Slicer engine owning the alias.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RemoveModelAliasAsync(
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
        if (!string.IsNullOrWhiteSpace(slicerType))
        {
            Guid exactMatch = await BuildMatchingAliasesQuery(
                    slicerModelName,
                    slicerType,
                    includeGeneric: false)
                .Select(a => a.PrinterModelId)
                .FirstOrDefaultAsync();

            if (exactMatch != Guid.Empty)
            {
                return exactMatch;
            }
        }

        // Fall back to slicer-agnostic alias (SlicerType is null, case-insensitive)
        Guid genericMatch = await BuildMatchingAliasesQuery(
                slicerModelName,
                slicerType: null,
                includeGeneric: false)
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

        List<PrinterModelAlias> existing = await BuildMatchingAliasesQuery(
                slicerModelName,
                slicerType,
                includeGeneric: true)
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

        Domain.PrinterModelAlias newAlias = new()
        {
            Id = Guid.NewGuid(),
            PrinterModelId = printerModelId,
            SlicerModelName = slicerModelName.Trim(),
            SlicerType = slicerType.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _ = _dbContext.PrinterModelAliases.Add(newAlias);

        try
        {
            _ = await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // #2080 N-NORM-1 (review finding): this method's own check-then-insert above is not
            // atomic, so a concurrent caller can insert the same normalized alias between our
            // check and this save. The unique index on the normalized columns now makes that a
            // real, reachable race (previously it raced on the raw columns instead, but the
            // failure mode was identical). Detach the entity the failed save left tracked as
            // Added -- otherwise it would be resubmitted (and fail again) on the context's next
            // SaveChangesAsync -- then re-check under the constraint: if the alias now exists
            // and maps to the same model, this call is idempotently satisfied; if it maps to a
            // different model, surface the same conflict the pre-check would have thrown had it
            // observed the row first.
            _dbContext.Entry(newAlias).State = EntityState.Detached;

            List<PrinterModelAlias> nowExisting = await BuildMatchingAliasesQuery(
                    slicerModelName,
                    slicerType,
                    includeGeneric: true)
                .ToListAsync(ct);
            if (nowExisting.Any(alias => alias.PrinterModelId != printerModelId))
            {
                throw new InvalidOperationException(
                    $"Slicer alias '{slicerModelName}' is already mapped to another printer model.",
                    ex);
            }

            if (nowExisting.Count == 0)
            {
                // The unique-constraint violation was not caused by an alias row (e.g. a
                // transient/unrelated failure) -- rethrow rather than silently swallowing it.
                throw;
            }
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx
            && sqliteEx.SqliteErrorCode == 19)
        {
            return true;
        }

        if (ex.InnerException is System.Data.Common.DbException dbException)
        {
            string typeName = dbException.GetType().FullName ?? string.Empty;
            if (typeName.Contains("SqlException", StringComparison.OrdinalIgnoreCase))
            {
                // #2080 N-NORM-1 (review finding, Vasquez): SqlException does not override
                // DbException.ErrorCode -- that property still returns the base Exception
                // HResult, not the SQL Server error number. The actual server-side error number
                // (2601/2627 for a unique-index/constraint violation) is only exposed via the
                // SqlException.Number property, so it must be read via reflection here (the same
                // way this method already avoids a direct Microsoft.Data.SqlClient reference).
                object? numberValue = dbException.GetType().GetProperty("Number")?.GetValue(dbException);
                if (numberValue is int number && number is 2601 or 2627)
                {
                    return true;
                }
            }
        }

        return ex.InnerException?.GetType().FullName?.Contains(
                "PostgresException", StringComparison.OrdinalIgnoreCase) == true
            && ex.InnerException.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException)?.ToString()
                == "23505";
    }

    /// <inheritdoc />
    public async Task RemoveModelAliasAsync(
        Guid printerModelId,
        string slicerModelName,
        string slicerType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slicerModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(slicerType);

        string normalizedModelName = PrinterModelAlias.NormalizeLookupValue(slicerModelName);
        string normalizedSlicerType = PrinterModelAlias.NormalizeLookupValue(slicerType);

        // Tracked query (not AsNoTracking) so the matches can be removed. Only exact
        // slicer-type aliases for this catalog model are eligible; generic aliases and aliases
        // pointing elsewhere are intentionally preserved.
        List<PrinterModelAlias> matches = await _dbContext.PrinterModelAliases
            .Where(alias =>
                alias.PrinterModelId == printerModelId
                && alias.SlicerModelNameNormalized == normalizedModelName
                && alias.SlicerTypeNormalized == normalizedSlicerType)
            .ToListAsync(ct);

        if (matches.Count == 0)
        {
            return;
        }

        _dbContext.PrinterModelAliases.RemoveRange(matches);
        _ = await _dbContext.SaveChangesAsync(ct);
    }

    internal IQueryable<PrinterModelAlias> BuildMatchingAliasesQuery(
        string slicerModelName,
        string? slicerType,
        bool includeGeneric)
    {
        string normalizedModelName =
            PrinterModelAlias.NormalizeLookupValue(slicerModelName);
        IQueryable<PrinterModelAlias> aliases =
            _dbContext.PrinterModelAliases
                .AsNoTracking()
                .Where(alias =>
                    alias.SlicerModelNameNormalized == normalizedModelName);
        if (slicerType is null)
        {
            return aliases.Where(alias => alias.SlicerTypeNormalized == null);
        }

        string normalizedSlicerType =
            PrinterModelAlias.NormalizeLookupValue(slicerType);
        return includeGeneric
            ? aliases.Where(alias =>
                alias.SlicerTypeNormalized == null
                || alias.SlicerTypeNormalized == normalizedSlicerType)
            : aliases.Where(alias =>
                alias.SlicerTypeNormalized == normalizedSlicerType);
    }
}
