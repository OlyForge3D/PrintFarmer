using System.Data.Common;
using System.Reflection;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Service for managing per-printer filament fallback groups (issue #711, F6).
/// Reuses the existing <see cref="Toolhead"/> hierarchy rather than introducing a
/// duplicate slot model.
/// </summary>
public interface IFilamentFallbackGroupService
{
    Task<IReadOnlyList<FilamentFallbackGroupDto>> ListForPrinterAsync(Guid printerId, CancellationToken ct);

    Task<FilamentFallbackGroupDto?> GetAsync(Guid printerId, Guid groupId, CancellationToken ct);

    Task<FilamentFallbackGroupDto> CreateAsync(Guid printerId, CreateFilamentFallbackGroupRequest request, CancellationToken ct);

    Task<FilamentFallbackGroupDto> UpdateAsync(Guid printerId, Guid groupId, UpdateFilamentFallbackGroupRequest request, CancellationToken ct);

    Task DeleteAsync(Guid printerId, Guid groupId, CancellationToken ct);

    /// <summary>
    /// Attempts to find a same-printer toolhead (physical dock or MMU/AMS gate) configured as
    /// a fallback that currently has a spool loaded matching the requested material. Returns
    /// <c>null</c> when no such backup exists. The caller MUST NOT infer a successful
    /// auto-switch from this result alone — configuration existence is not proof that a switch
    /// occurred. This method exists so filament-runout severity downgrade paths can require
    /// both confirmed telemetry AND a truly available configured backup (issue #711, F6).
    /// </summary>
    /// <remarks>
    /// Exposed to external callers via
    /// <c>GET /api/printers/{printerId}/fallback-groups/available</c> (gated by the
    /// multi-slot-fallback operator feature). TODO(#711): wire this resolver into the
    /// filament runout attention source (see <c>FilamentRunoutAttentionSource</c>) so that when
    /// auto-switch telemetry is unavailable, an informational attention item can point the
    /// operator at an available fallback slot. That integration is deferred because it requires
    /// resolving a runout warning's toolhead <em>index</em> to a toolhead <em>id</em> per
    /// printer inside the attention pipeline and reconciling two independent feature gates
    /// (filament-coverage vs multi-slot-fallback); the read-only API endpoint gives the
    /// resolver a production caller in the meantime.
    /// </remarks>
    /// <param name="printerId">Owning printer.</param>
    /// <param name="sourceToolheadId">The toolhead that ran out; excluded from the search.</param>
    /// <param name="materialType">Material required to keep printing (case-insensitive).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AvailableFallbackMember?> FindAvailableFallbackAsync(
        Guid printerId,
        Guid sourceToolheadId,
        string materialType,
        CancellationToken ct);

    /// <summary>
    /// Loads configured fallback chains for all candidate printers in one database query.
    /// Results are keyed by printer, source toolhead, and normalized material so dispatch
    /// scoring can reuse the same ordered chain without issuing per-printer/per-tool queries.
    /// </summary>
    Task<IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution>>
        GetAvailableFallbacksAsync(IEnumerable<Guid> printerIds, CancellationToken ct);
}

/// <summary>
/// Normalized lookup key for one source toolhead's fallback chain.
/// </summary>
public readonly record struct FilamentFallbackLookupKey(
    Guid PrinterId,
    Guid SourceToolheadId,
    string Material)
{
    public static FilamentFallbackLookupKey Create(
        Guid printerId,
        Guid sourceToolheadId,
        string material) =>
        new(printerId, sourceToolheadId, material.Trim().ToUpperInvariant());
}

/// <summary>
/// Configured members after a source toolhead in one ordered fallback chain.
/// </summary>
public sealed record FilamentFallbackResolution(
    Guid GroupId,
    IReadOnlyList<FilamentFallbackChainMember> Members);

/// <summary>
/// One configured member in an ordered fallback chain. Persisted loadout fields are included as
/// backup-configuration evidence only; live switch confirmation must come from backend telemetry.
/// </summary>
public sealed record FilamentFallbackChainMember(
    Guid GroupId,
    Guid MemberId,
    Guid ToolheadId,
    int Position,
    string? LoadedMaterial,
    int? LoadedSpoolId);

/// <summary>
/// A concrete configured fallback toolhead that currently has a spool loaded matching
/// the requested material. Used only as evidence of an available backup — never as
/// confirmation that a switch happened.
/// </summary>
public sealed record AvailableFallbackMember(
    Guid GroupId,
    Guid MemberId,
    Guid ToolheadId,
    int Position,
    string LoadedMaterial,
    int? LoadedSpoolId);

/// <summary>
/// Thrown when a fallback-group mutation violates the ownership/ordering/uniqueness rules.
/// </summary>
#pragma warning disable CA1032 // exception is used exclusively for validation with a single message contract
public sealed class FilamentFallbackGroupValidationException(string message) : InvalidOperationException(message);
#pragma warning restore CA1032

public sealed class FilamentFallbackGroupService(
    AppDbContext db,
    ILogger<FilamentFallbackGroupService> logger) : IFilamentFallbackGroupService
{
    public async Task<IReadOnlyList<FilamentFallbackGroupDto>> ListForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        List<FilamentFallbackGroup> groups = await db.FilamentFallbackGroups
            .AsNoTracking()
            .Where(g => g.PrinterId == printerId)
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.CreatedAt)
            .Include(g => g.Members.OrderBy(m => m.Position))
                .ThenInclude(m => m.Toolhead)
            .ToListAsync(ct);

        return [.. groups.Select(MapGroup)];
    }

    public async Task<FilamentFallbackGroupDto?> GetAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        FilamentFallbackGroup? group = await db.FilamentFallbackGroups
            .AsNoTracking()
            .Include(g => g.Members.OrderBy(m => m.Position))
                .ThenInclude(m => m.Toolhead)
            .FirstOrDefaultAsync(g => g.PrinterId == printerId && g.Id == groupId, ct);

        return group is null ? null : MapGroup(group);
    }

    public async Task<FilamentFallbackGroupDto> CreateAsync(Guid printerId, CreateFilamentFallbackGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBasic(request.Name, request.MaterialType, request.ToolheadIds);

        Printer? printer = await db.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new KeyNotFoundException($"Printer {printerId} not found.");

        // Enforce ownership and topology-aware filament-source eligibility. Physical docks remain
        // valid on traditional/toolchanger printers, but an MMU printer carries filament only in
        // its gates; its shared physical hotend cannot participate in a fallback chain.
        Dictionary<Guid, Toolhead> printerToolheadsById = printer.Toolheads.ToDictionary(t => t.Id);
        foreach (Guid id in request.ToolheadIds)
        {
            if (!printerToolheadsById.TryGetValue(id, out Toolhead? th))
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} does not belong to printer {printerId}.");
            }

            if (!ToolheadIndexMapper.IsFilamentSource(th, printer.Toolheads))
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} is not a filament source in this printer topology and cannot participate in a fallback group.");
            }
        }

        // Enforce name uniqueness within this printer (case-insensitive).
        string trimmedName = request.Name.Trim();
        string trimmedNameLower = trimmedName.ToLowerInvariant();
#pragma warning disable CA1862 // EF Core translates ToLower to SQL LOWER for case-insensitive comparison.
        bool nameTaken = await db.FilamentFallbackGroups
            .AnyAsync(
                g => g.PrinterId == printerId && g.Name.ToLower() == trimmedNameLower,
                ct);
#pragma warning restore CA1862
        if (nameTaken)
        {
            throw new FilamentFallbackGroupValidationException(
                $"A fallback group named '{trimmedName}' already exists on this printer.");
        }

        FilamentFallbackGroup group = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = trimmedName,
            NameNormalized = trimmedNameLower,
            MaterialType = request.MaterialType.Trim(),
            DisplayOrder = request.DisplayOrder ?? await NextDisplayOrderAsync(printerId, ct),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        int position = 0;
        foreach (Guid toolheadId in request.ToolheadIds)
        {
            group.Members.Add(new FilamentFallbackGroupMember
            {
                Id = Guid.NewGuid(),
                FallbackGroupId = group.Id,
                ToolheadId = toolheadId,
                Position = position++,
            });
        }

        _ = db.FilamentFallbackGroups.Add(group);
        await SaveChangesTranslatingUniqueViolationsAsync(trimmedName, printerId, ct);
        logger.LogInformation(
            "Created filament fallback group {GroupId} on printer {PrinterId} with {Count} members.",
            group.Id,
            printerId,
            group.Members.Count);

        return (await GetAsync(printerId, group.Id, ct))!;
    }

    public async Task<FilamentFallbackGroupDto> UpdateAsync(Guid printerId, Guid groupId, UpdateFilamentFallbackGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBasic(request.Name, request.MaterialType, request.ToolheadIds);

        FilamentFallbackGroup group = await db.FilamentFallbackGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.PrinterId == printerId && g.Id == groupId, ct)
            ?? throw new KeyNotFoundException($"Fallback group {groupId} not found on printer {printerId}.");

        Printer? printer = await db.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct)
            ?? throw new KeyNotFoundException($"Printer {printerId} not found.");

        Dictionary<Guid, Toolhead> printerToolheadsById = printer.Toolheads.ToDictionary(t => t.Id);
        foreach (Guid id in request.ToolheadIds)
        {
            if (!printerToolheadsById.TryGetValue(id, out Toolhead? th))
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} does not belong to printer {printerId}.");
            }

            if (!ToolheadIndexMapper.IsFilamentSource(th, printer.Toolheads))
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} is not a filament source in this printer topology and cannot participate in a fallback group.");
            }
        }

        string trimmedName = request.Name.Trim();
        string trimmedNameLower = trimmedName.ToLowerInvariant();
#pragma warning disable CA1862 // EF Core translates ToLower to SQL LOWER for case-insensitive comparison.
        bool nameTaken = await db.FilamentFallbackGroups
            .AnyAsync(
                g => g.PrinterId == printerId && g.Id != groupId && g.Name.ToLower() == trimmedNameLower,
                ct);
#pragma warning restore CA1862
        if (nameTaken)
        {
            throw new FilamentFallbackGroupValidationException(
                $"A fallback group named '{trimmedName}' already exists on this printer.");
        }

        group.Name = trimmedName;
        group.NameNormalized = trimmedNameLower;
        group.MaterialType = request.MaterialType.Trim();
        if (request.DisplayOrder.HasValue)
        {
            group.DisplayOrder = request.DisplayOrder.Value;
        }

        group.UpdatedAt = DateTime.UtcNow;

        // Replace the member list; positions come from array order.
        db.FilamentFallbackGroupMembers.RemoveRange(group.Members);
        group.Members.Clear();

        int position = 0;
        foreach (Guid toolheadId in request.ToolheadIds)
        {
            group.Members.Add(new FilamentFallbackGroupMember
            {
                Id = Guid.NewGuid(),
                FallbackGroupId = group.Id,
                ToolheadId = toolheadId,
                Position = position++,
            });
        }

        await SaveChangesTranslatingUniqueViolationsAsync(trimmedName, printerId, ct);
        logger.LogInformation(
            "Updated filament fallback group {GroupId} on printer {PrinterId} (members={Count}).",
            group.Id,
            printerId,
            group.Members.Count);

        return (await GetAsync(printerId, group.Id, ct))!;
    }

    public async Task DeleteAsync(Guid printerId, Guid groupId, CancellationToken ct)
    {
        FilamentFallbackGroup? group = await db.FilamentFallbackGroups
            .FirstOrDefaultAsync(g => g.PrinterId == printerId && g.Id == groupId, ct);
        if (group is null)
        {
            return;
        }

        _ = db.FilamentFallbackGroups.Remove(group);
        _ = await db.SaveChangesAsync(ct);
        logger.LogInformation("Deleted filament fallback group {GroupId} on printer {PrinterId}.", groupId, printerId);
    }

    public async Task<AvailableFallbackMember?> FindAvailableFallbackAsync(
        Guid printerId,
        Guid sourceToolheadId,
        string materialType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(materialType))
        {
            return null;
        }

        IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution> resolutions =
            await GetAvailableFallbacksAsync([printerId], ct).ConfigureAwait(false);
        FilamentFallbackLookupKey key =
            FilamentFallbackLookupKey.Create(printerId, sourceToolheadId, materialType);
        _ = resolutions.TryGetValue(key, out FilamentFallbackResolution? resolution);
        FilamentFallbackChainMember? member = resolution?.Members.FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.LoadedMaterial)
                && string.Equals(
                    candidate.LoadedMaterial,
                    materialType,
                    StringComparison.OrdinalIgnoreCase));
        return member is null
            ? null
            : new AvailableFallbackMember(
                member.GroupId,
                member.MemberId,
                member.ToolheadId,
                member.Position,
                member.LoadedMaterial!,
                member.LoadedSpoolId);
    }

    public async Task<IReadOnlyDictionary<FilamentFallbackLookupKey, FilamentFallbackResolution>>
        GetAvailableFallbacksAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(printerIds);

        Guid[] candidateIds = [.. printerIds.Distinct()];
        if (candidateIds.Length == 0)
        {
            return new Dictionary<FilamentFallbackLookupKey, FilamentFallbackResolution>();
        }

        List<FilamentFallbackGroup> groups = await db.FilamentFallbackGroups
            .AsNoTracking()
            .Where(g => candidateIds.Contains(g.PrinterId))
            .Include(g => g.Members.OrderBy(m => m.Position))
                .ThenInclude(m => m.Toolhead)
            .AsSingleQuery()
            .ToListAsync(ct);
        List<Toolhead> candidateToolheads = await db.Toolheads
            .AsNoTracking()
            .Where(t => candidateIds.Contains(t.PrinterId))
            .ToListAsync(ct);
        Dictionary<Guid, List<Toolhead>> toolheadsByPrinter = candidateToolheads
            .GroupBy(t => t.PrinterId)
            .ToDictionary(group => group.Key, group => group.ToList());

        Dictionary<FilamentFallbackLookupKey, FilamentFallbackResolution> resolutions = [];
        foreach (FilamentFallbackGroup group in groups
            .OrderBy(g => g.PrinterId)
            .ThenBy(g => g.DisplayOrder)
            .ThenBy(g => g.CreatedAt)
            .ThenBy(g => g.Id))
        {
            if (!toolheadsByPrinter.TryGetValue(
                group.PrinterId,
                out List<Toolhead>? printerToolheads))
            {
                continue;
            }

            List<FilamentFallbackGroupMember> orderedMembers =
            [
                .. group.Members
                    .Where(member =>
                        member.Toolhead is not null
                        && ToolheadIndexMapper.IsFilamentSource(
                            member.Toolhead,
                            printerToolheads))
                    .OrderBy(member => member.Position)
            ];
            for (int sourceIndex = 0; sourceIndex < orderedMembers.Count - 1; sourceIndex++)
            {
                FilamentFallbackGroupMember source = orderedMembers[sourceIndex];
                FilamentFallbackLookupKey key = FilamentFallbackLookupKey.Create(
                    group.PrinterId,
                    source.ToolheadId,
                    group.MaterialType);
                if (resolutions.ContainsKey(key))
                {
                    continue;
                }

                List<FilamentFallbackChainMember> chain = [];
                foreach (FilamentFallbackGroupMember member in orderedMembers.Skip(sourceIndex + 1))
                {
                    Toolhead? toolhead = member.Toolhead;
                    if (toolhead is null)
                    {
                        continue;
                    }

                    chain.Add(new FilamentFallbackChainMember(
                        group.Id,
                        member.Id,
                        member.ToolheadId,
                        member.Position,
                        toolhead.CurrentMaterial,
                        toolhead.CurrentSpoolId));
                }

                if (chain.Count > 0)
                {
                    resolutions[key] = new FilamentFallbackResolution(group.Id, chain);
                }
            }
        }

        return resolutions;
    }

    private async Task<int> NextDisplayOrderAsync(Guid printerId, CancellationToken ct)
    {
        int? maxOrder = await db.FilamentFallbackGroups
            .Where(g => g.PrinterId == printerId)
            .Select(g => (int?)g.DisplayOrder)
            .MaxAsync(ct);
        return (maxOrder ?? -1) + 1;
    }

    /// <summary>
    /// Persists pending changes, translating unique-constraint violations that slip past the
    /// check-then-write guards (concurrent creates/updates racing between the in-memory
    /// duplicate check and the commit) into <see cref="FilamentFallbackGroupValidationException"/>
    /// so callers get a 4xx validation error instead of an unhandled 500. The provider-specific
    /// error text is matched by index name (PostgreSQL/SQL Server) or table+column hints
    /// (SQLite) so the mapping is portable. Issue #711 (F6 remediation).
    /// </summary>
    private async Task SaveChangesTranslatingUniqueViolationsAsync(string groupName, Guid printerId, CancellationToken ct)
    {
        try
        {
            _ = await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (TryTranslateUniqueViolation(ex, groupName) is { } translated)
        {
            logger.LogWarning(
                ex,
                "Concurrent unique-constraint violation persisting fallback group '{GroupName}' on printer {PrinterId}; surfacing as validation error.",
                LogSanitizer.Sanitize(groupName),
                printerId);
            throw translated;
        }
    }

    private static FilamentFallbackGroupValidationException? TryTranslateUniqueViolation(DbUpdateException ex, string groupName)
    {
        // Only translate genuine UNIQUE-constraint violations. Matching on inner-exception
        // message tokens alone risks false positives — e.g. SQL Server 2628 (string-or-binary
        // truncation) also names the offending column and would be mistaken for "already
        // exists". First confirm the provider reported a unique violation via its numeric
        // error code / SQLSTATE, THEN use the constraint/index name to select the specific
        // message. If the error is not a unique violation, return null so the caller rethrows
        // the original DbUpdateException. Issue #711 (F6 remediation, FIX F).
        if (!IsUniqueConstraintViolation(ex))
        {
            return null;
        }

        string detail = $"{ex.InnerException?.Message} {ex.Message}";

        bool Mentions(params string[] tokens) =>
            tokens.All(t => detail.Contains(t, StringComparison.OrdinalIgnoreCase));

        // Two members claim the same ordered position within the group.
        if (detail.Contains("UX_FilamentFallbackGroupMembers_GroupId_Position", StringComparison.OrdinalIgnoreCase)
            || Mentions("FilamentFallbackGroupMembers", "Position"))
        {
            return new FilamentFallbackGroupValidationException(
                "A fallback group member position conflict occurred; please retry.");
        }

        // The same toolhead is referenced more than once within the group.
        if (detail.Contains("UX_FilamentFallbackGroupMembers_GroupId_ToolheadId", StringComparison.OrdinalIgnoreCase)
            || Mentions("FilamentFallbackGroupMembers", "ToolheadId"))
        {
            return new FilamentFallbackGroupValidationException(
                "Fallback group members must reference each toolhead at most once.");
        }

        // Name collision within the printer (case-insensitive, enforced via the NameNormalized
        // unique index). This is the default for a confirmed unique violation that is not one
        // of the member-index conflicts handled above — the normalized-name index is the
        // operator-facing uniqueness rule.
        return new FilamentFallbackGroupValidationException(
            $"A fallback group named '{groupName}' already exists on this printer.");
    }

    /// <summary>
    /// Walks the inner-exception chain and returns <c>true</c> only when a provider reported a
    /// UNIQUE-constraint violation, identified by provider-specific error codes rather than
    /// message text. SQLite is matched by its extended error code; PostgreSQL by SQLSTATE
    /// (surfaced on the ADO.NET base <see cref="DbException.SqlState"/> by Npgsql); SQL Server
    /// by its numeric error number (read reflectively so this core project needs no direct
    /// SqlClient reference). Issue #711 (F6, FIX F).
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                // SQLite (local dev / tests): SQLITE_CONSTRAINT (19) with extended
                // SQLITE_CONSTRAINT_UNIQUE (2067).
                case SqliteException sqlite
                    when sqlite.SqliteErrorCode == 19 && sqlite.SqliteExtendedErrorCode == 2067:
                    return true;

                // PostgreSQL: SQLSTATE 23505 (unique_violation).
                case DbException pg when string.Equals(pg.SqlState, "23505", StringComparison.Ordinal):
                    return true;

                // SQL Server: 2601 (unique index) or 2627 (unique/PK constraint).
                case DbException sql when TryGetSqlServerErrorNumber(sql) is 2601 or 2627:
                    return true;
            }
        }

        return false;
    }

    private static int? TryGetSqlServerErrorNumber(DbException ex)
    {
        // Microsoft.Data.SqlClient.SqlException exposes a `Number` (int) property that is not on
        // the ADO.NET base type. Read it reflectively so the infrastructure project does not
        // need a direct SqlClient package reference (provider packages live in the migration
        // projects to keep infra provider-agnostic).
        PropertyInfo? prop = ex.GetType().GetProperty("Number", typeof(int));
        return prop?.GetValue(ex) as int?;
    }

    private static void ValidateBasic(string name, string materialType, IReadOnlyList<Guid> toolheadIds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FilamentFallbackGroupValidationException("Fallback group name is required.");
        }

        if (name.Trim().Length > 128)
        {
            throw new FilamentFallbackGroupValidationException(
                "Fallback group name must be 128 characters or fewer.");
        }

        if (string.IsNullOrWhiteSpace(materialType))
        {
            throw new FilamentFallbackGroupValidationException("Fallback group material type is required.");
        }

        if (materialType.Trim().Length > 64)
        {
            throw new FilamentFallbackGroupValidationException(
                "Fallback group material type must be 64 characters or fewer.");
        }

        if (toolheadIds is null || toolheadIds.Count < 2)
        {
            throw new FilamentFallbackGroupValidationException(
                "A fallback group requires at least two toolhead members (primary + fallback).");
        }

        HashSet<Guid> distinct = [.. toolheadIds];
        if (distinct.Count != toolheadIds.Count)
        {
            throw new FilamentFallbackGroupValidationException(
                "Fallback group members must reference each toolhead at most once.");
        }
    }

    internal static FilamentFallbackGroupDto MapGroup(FilamentFallbackGroup g)
    {
        string materialType = g.MaterialType ?? string.Empty;
        List<FilamentFallbackGroupMemberDto> members = [.. g.Members
            .OrderBy(m => m.Position)
            .Select(m => MapMember(m, materialType))];

        return new FilamentFallbackGroupDto(
            g.Id,
            g.PrinterId,
            g.Name,
            materialType,
            g.DisplayOrder,
            g.CreatedAt,
            g.UpdatedAt,
            members);
    }

    private static FilamentFallbackGroupMemberDto MapMember(FilamentFallbackGroupMember m, string materialType)
    {
        bool materialMatches = !string.IsNullOrWhiteSpace(m.Toolhead?.CurrentMaterial)
            && string.Equals(m.Toolhead.CurrentMaterial, materialType, StringComparison.OrdinalIgnoreCase);
        return new FilamentFallbackGroupMemberDto(
            m.Id,
            m.ToolheadId,
            m.Position,
            m.Toolhead?.Name,
            m.Toolhead?.Index ?? 0,
            m.Toolhead?.CurrentMaterial,
            m.Toolhead?.CurrentSpoolId,
            materialMatches);
    }
}
