using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Normalization;
using Farm.Infrastructure.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Persists exact-match scan-code aliases in PrintFarmer's database. Spoolman is read only to
/// confirm the target filament exists; nothing is written to Spoolman.
/// </summary>
public class ScanCodeAliasService(
    IDbContextFactory<AppDbContext> dbContextFactory,
    ISpoolmanService spoolmanService,
    ILogger<ScanCodeAliasService> logger) : IScanCodeAliasService
{
    public async Task<ScanCodeAliasLookupResult> LookupAsync(string? code, CancellationToken ct)
    {
        string? normalized = ScanCodeAliasValidator.Validate(code, out string? error);
        if (normalized is null)
        {
            return new(ScanCodeAliasLookupStatus.Invalid, null, null, error);
        }

        ScanCodeAlias? alias = await FindAsync(normalized, ct);
        if (alias is null)
        {
            return new(ScanCodeAliasLookupStatus.NotFound, null, null, null);
        }

        SpoolmanFilamentDto? filament = await spoolmanService.FindFilamentByIdAsync(alias.FilamentId, ct);
        return filament is null
            ? new(ScanCodeAliasLookupStatus.FilamentMissing, alias, null, $"Filament {alias.FilamentId} no longer exists.")
            : new(ScanCodeAliasLookupStatus.Found, alias, filament, null);
    }

    public async Task<ScanCodeAliasSaveResult> SaveAsync(string? code, int filamentId, string? userId, CancellationToken ct)
    {
        string? normalized = ScanCodeAliasValidator.Validate(code, out string? error);
        if (normalized is null)
        {
            return new(ScanCodeAliasSaveStatus.Invalid, null, null, error);
        }

        if (filamentId <= 0)
        {
            return new(ScanCodeAliasSaveStatus.Invalid, null, null, "A valid filament ID is required.");
        }

        SpoolmanFilamentDto? filament = await spoolmanService.FindFilamentByIdAsync(filamentId, ct);
        if (filament is null)
        {
            return new(ScanCodeAliasSaveStatus.FilamentNotFound, null, null, $"Filament {filamentId} was not found.");
        }

        await using AppDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        ScanCodeAlias? existing = await db.ScanCodeAliases.SingleOrDefaultAsync(a => a.Code == normalized, ct);
        if (existing is not null && !IsExactMatch(existing, normalized))
        {
            existing = null;
        }

        if (existing is not null)
        {
            if (existing.FilamentId == filamentId)
            {
                return new(ScanCodeAliasSaveStatus.AlreadyMapped, existing, filament, null);
            }

            SpoolmanFilamentDto? current = await spoolmanService.FindFilamentByIdAsync(existing.FilamentId, ct);
            if (current is not null)
            {
                return new(
                    ScanCodeAliasSaveStatus.Conflict,
                    existing,
                    current,
                    $"Code is already remembered for filament {existing.FilamentId}.");
            }

            logger.LogInformation(
                "Replacing scan-code alias {AliasId}: filament {OldFilamentId} no longer exists",
                existing.Id,
                existing.FilamentId);

            // Compare-and-swap on the stale filament ID so two clients replacing the same dead
            // link can't silently overwrite each other: only one swap succeeds.
            int staleFilamentId = existing.FilamentId;
            int aliasId = existing.Id;
            DateTime replacedAt = DateTime.UtcNow;
            string? replacedBy = Truncate(userId);
            int updated = await db.ScanCodeAliases
                .Where(a => a.Id == aliasId && a.FilamentId == staleFilamentId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(a => a.FilamentId, filamentId)
                        .SetProperty(a => a.UpdatedAt, replacedAt)
                        .SetProperty(a => a.CreatedByUserId, replacedBy),
                    ct);
            if (updated == 0)
            {
                logger.LogInformation("Concurrent scan-code alias replacement detected for alias {AliasId}", aliasId);
                return await ResolveConcurrentWinnerAsync(normalized, filamentId, filament, ct);
            }

            ScanCodeAlias? replaced = await FindAsync(normalized, ct);
            return new(ScanCodeAliasSaveStatus.Replaced, replaced, filament, null);
        }

        DateTime now = DateTime.UtcNow;
        var alias = new ScanCodeAlias
        {
            Code = normalized,
            FilamentId = filamentId,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = Truncate(userId),
        };
        _ = db.ScanCodeAliases.Add(alias);
        try
        {
            _ = await db.SaveChangesAsync(ct);
            return new(ScanCodeAliasSaveStatus.Created, alias, filament, null);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent save won the unique index; report whatever it stored.
            logger.LogInformation(ex, "Concurrent scan-code alias save detected");
            if (await FindAsync(normalized, ct) is null)
            {
                throw;
            }

            return await ResolveConcurrentWinnerAsync(normalized, filamentId, filament, ct);
        }
    }

    private async Task<ScanCodeAliasSaveResult> ResolveConcurrentWinnerAsync(
        string code,
        int filamentId,
        SpoolmanFilamentDto filament,
        CancellationToken ct)
    {
        ScanCodeAlias? winner = await FindAsync(code, ct)
            ?? throw new InvalidOperationException($"Scan-code alias for '{code}' disappeared during a concurrent save.");
        return winner.FilamentId == filamentId
            ? new(ScanCodeAliasSaveStatus.AlreadyMapped, winner, filament, null)
            : new(
                ScanCodeAliasSaveStatus.Conflict,
                winner,
                null,
                $"Code is already remembered for filament {winner.FilamentId}.");
    }

    private async Task<ScanCodeAlias?> FindAsync(string code, CancellationToken ct)
    {
        await using AppDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        ScanCodeAlias? alias = await db.ScanCodeAliases.AsNoTracking().SingleOrDefaultAsync(a => a.Code == code, ct);
        return alias is not null && IsExactMatch(alias, code) ? alias : null;
    }

    // Defence in depth: the column is byte-exact on every provider, but never trust a
    // case-insensitive collation to decide identity.
    private static bool IsExactMatch(ScanCodeAlias alias, string code)
        => string.Equals(alias.Code, code, StringComparison.Ordinal);

    private static string? Truncate(string? value)
        => value is { Length: > 450 } ? value[..450] : value;
}
