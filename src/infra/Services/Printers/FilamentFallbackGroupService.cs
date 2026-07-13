using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
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
    /// Attempts to find a same-printer physical toolhead configured as a fallback that
    /// currently has a spool loaded matching the requested material. Returns <c>null</c>
    /// when no such backup exists. The caller MUST NOT infer a successful auto-switch from
    /// this result alone — configuration existence is not proof that a switch occurred.
    /// This method exists so filament-runout severity downgrade paths can require both
    /// confirmed telemetry AND a truly available configured backup (issue #711, F6).
    /// </summary>
    /// <param name="printerId">Owning printer.</param>
    /// <param name="sourceToolheadId">The toolhead that ran out; excluded from the search.</param>
    /// <param name="materialType">Material required to keep printing (case-insensitive).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AvailableFallbackMember?> FindAvailableFallbackAsync(
        Guid printerId,
        Guid sourceToolheadId,
        string materialType,
        CancellationToken ct);
}

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

        // Enforce toolhead ownership (all members belong to this printer) and physical-only
        // membership. MMU virtual gates are not eligible as fallback destinations; the fallback
        // chain represents alternative physical extruders/hotends that can carry the same
        // material. See issue #711 (F6).
        Dictionary<Guid, Toolhead> printerToolheadsById = printer.Toolheads.ToDictionary(t => t.Id);
        foreach (Guid id in request.ToolheadIds)
        {
            if (!printerToolheadsById.TryGetValue(id, out Toolhead? th))
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} does not belong to printer {printerId}.");
            }

            if (th.ToolheadType != ToolheadType.Physical)
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} is not a physical toolhead and cannot participate in a fallback group.");
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

            if (th.ToolheadType != ToolheadType.Physical)
            {
                throw new FilamentFallbackGroupValidationException(
                    $"Toolhead {id} is not a physical toolhead and cannot participate in a fallback group.");
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

        string materialLower = materialType.ToLowerInvariant();
#pragma warning disable CA1862 // EF Core translates ToLower to SQL LOWER for case-insensitive comparison.
        List<FilamentFallbackGroup> groups = await db.FilamentFallbackGroups
            .AsNoTracking()
            .Where(g => g.PrinterId == printerId
                && g.MaterialType.ToLower() == materialLower
                && g.Members.Any(m => m.ToolheadId == sourceToolheadId))
            .Include(g => g.Members.OrderBy(m => m.Position))
                .ThenInclude(m => m.Toolhead)
            .ToListAsync(ct);
#pragma warning restore CA1862

        foreach (FilamentFallbackGroup g in groups.OrderBy(g => g.DisplayOrder))
        {
            foreach (FilamentFallbackGroupMember member in g.Members.OrderBy(m => m.Position))
            {
                if (member.ToolheadId == sourceToolheadId)
                {
                    continue;
                }

                Toolhead? th = member.Toolhead;
                if (th is null || th.ToolheadType != ToolheadType.Physical)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(th.CurrentMaterial)
                    && string.Equals(th.CurrentMaterial, materialType, StringComparison.OrdinalIgnoreCase))
                {
                    return new AvailableFallbackMember(
                        g.Id,
                        member.Id,
                        member.ToolheadId,
                        member.Position,
                        th.CurrentMaterial!,
                        th.CurrentSpoolId);
                }
            }
        }

        return null;
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
                groupName,
                printerId);
            throw translated;
        }
    }

    private static FilamentFallbackGroupValidationException? TryTranslateUniqueViolation(DbUpdateException ex, string groupName)
    {
        string detail = $"{ex.InnerException?.Message} {ex.Message}";

        bool Mentions(params string[] tokens) =>
            tokens.All(t => detail.Contains(t, StringComparison.OrdinalIgnoreCase));

        // Name collision within the printer.
        if (detail.Contains("UX_FilamentFallbackGroups_PrinterId_Name", StringComparison.OrdinalIgnoreCase)
            || Mentions("FilamentFallbackGroups", "Name"))
        {
            return new FilamentFallbackGroupValidationException(
                $"A fallback group named '{groupName}' already exists on this printer.");
        }

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

        return null;
    }

    private static void ValidateBasic(string name, string materialType, IReadOnlyList<Guid> toolheadIds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new FilamentFallbackGroupValidationException("Fallback group name is required.");
        }

        if (string.IsNullOrWhiteSpace(materialType))
        {
            throw new FilamentFallbackGroupValidationException("Fallback group material type is required.");
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
