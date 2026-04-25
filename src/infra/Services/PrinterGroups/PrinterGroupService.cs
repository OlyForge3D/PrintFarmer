using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrinterGroups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.PrinterGroups;

/// <summary>
/// Service layer for printer group operations with business rule enforcement.
/// </summary>
public class PrinterGroupService(
    IPrinterGroupRepository repository,
    AppDbContext db,
    ILogger<PrinterGroupService> logger) : IPrinterGroupService
{
    public async Task<IReadOnlyList<PrinterGroupDto>> ListAllAsync(CancellationToken ct)
    {
        IReadOnlyList<PrinterGroup> groups = await repository.ListAllAsync(ct);
        return groups.Select(MapToDto).ToList();
    }

    public async Task<PrinterGroupDetailDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterGroup? group = await repository.GetByIdAsync(id, ct);
        return group is null ? null : MapToDetailDto(group);
    }

    public async Task<PrinterGroupDto> CreateAsync(CreatePrinterGroupDto dto, CancellationToken ct)
    {
        string trimmedName = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Group name is required.");
        }

        PrinterGroup? existing = await repository.GetByNameAsync(trimmedName, ct);
        if (existing is not null)
        {
            throw new InvalidOperationException($"A printer group named '{trimmedName}' already exists.");
        }

        var group = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Description = dto.Description?.Trim(),
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow,
        };

        await repository.AddAsync(group, ct);
        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Created printer group '{Name}' ({Id})", group.Name, group.Id);
        return MapToDto(group);
    }

    public async Task<PrinterGroupDto> UpdateAsync(Guid id, UpdatePrinterGroupDto dto, CancellationToken ct)
    {
        PrinterGroup? group = await repository.GetByIdAsync(id, ct);
        if (group is null)
        {
            throw new KeyNotFoundException($"Printer group {id} not found.");
        }

        string trimmedName = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Group name is required.");
        }

        // Check uniqueness only if the name changed
        if (!string.Equals(group.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
        {
            PrinterGroup? conflict = await repository.GetByNameAsync(trimmedName, ct);
            if (conflict is not null)
            {
                throw new InvalidOperationException($"A printer group named '{trimmedName}' already exists.");
            }
        }

        group.Name = trimmedName;
        group.Description = dto.Description?.Trim();
        group.UpdatedDate = DateTimeOffset.UtcNow;

        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Updated printer group '{Name}' ({Id})", group.Name, group.Id);
        return MapToDto(group);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        PrinterGroup? group = await repository.GetByIdAsync(id, ct);
        if (group is null)
        {
            throw new KeyNotFoundException($"Printer group {id} not found.");
        }

        repository.Remove(group);
        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Deleted printer group '{Name}' ({Id})", group.Name, group.Id);
    }

    public async Task AddPrinterAsync(Guid groupId, Guid printerId, CancellationToken ct)
    {
        PrinterGroup? group = await repository.GetByIdAsync(groupId, ct);
        if (group is null)
        {
            throw new KeyNotFoundException($"Printer group {groupId} not found.");
        }

        Printer? printer = await db.Printers
            .Include(p => p.Model)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);
        if (printer is null)
        {
            throw new KeyNotFoundException($"Printer {printerId} not found.");
        }

        // Enforce homogeneous groups: all printers must share the same model
        Printer? existingMember = await db.Printers
            .Include(p => p.Model)
            .Where(p => p.PrinterGroupId == groupId && p.Id != printerId)
            .FirstOrDefaultAsync(ct);

        if (existingMember is not null && existingMember.ModelId != printer.ModelId)
        {
            string existingModelName = existingMember.Model?.Name ?? "Unknown";
            string newModelName = printer.Model?.Name ?? "Unknown";
            throw new InvalidOperationException(
                $"All printers in a group must be the same model. " +
                $"This group contains {existingModelName} printers, but '{printer.Name}' is a {newModelName}.");
        }

        printer.PrinterGroupId = groupId;
        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Added printer '{PrinterName}' to group '{GroupName}'", printer.Name, group.Name);
    }

    public async Task RemovePrinterAsync(Guid groupId, Guid printerId, CancellationToken ct)
    {
        PrinterGroup? group = await repository.GetByIdAsync(groupId, ct);
        if (group is null)
        {
            throw new KeyNotFoundException($"Printer group {groupId} not found.");
        }

        Printer? printer = await db.Printers.FirstOrDefaultAsync(p => p.Id == printerId && p.PrinterGroupId == groupId, ct);
        if (printer is null)
        {
            throw new KeyNotFoundException($"Printer {printerId} not found in group {groupId}.");
        }

        printer.PrinterGroupId = null;
        await repository.SaveChangesAsync(ct);

        logger.LogInformation("Removed printer '{PrinterName}' from group '{GroupName}'", printer.Name, group.Name);
    }

    public async Task<IReadOnlyList<PrinterGroupAccessDto>> GetAccessRulesAsync(Guid groupId, CancellationToken ct)
    {
        List<PrinterGroupAccess> rules = await db.PrinterGroupAccesses
            .Include(a => a.Role)
            .Where(a => a.PrinterGroupId == groupId)
            .OrderBy(a => a.Role.Name)
            .ThenBy(a => a.AccessLevel)
            .ToListAsync(ct);

        return rules.Select(a => new PrinterGroupAccessDto(
            a.Id,
            a.RoleId,
            a.Role.Name,
            a.AccessLevel,
            a.CreatedDate)).ToList();
    }

    public async Task<IReadOnlyList<PrinterGroupAccessDto>> SetAccessRulesAsync(Guid groupId, SetAccessRulesDto dto, CancellationToken ct)
    {
        bool groupExists = await db.PrinterGroups.AnyAsync(g => g.Id == groupId, ct);
        if (!groupExists)
        {
            throw new KeyNotFoundException($"Printer group {groupId} not found.");
        }

        // Replace-all: remove existing rules, insert new ones
        List<PrinterGroupAccess> existing = await db.PrinterGroupAccesses
            .Where(a => a.PrinterGroupId == groupId)
            .ToListAsync(ct);
        db.PrinterGroupAccesses.RemoveRange(existing);

        List<PrinterGroupAccess> newRules = dto.Rules.Select(r => new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = groupId,
            RoleId = r.RoleId,
            AccessLevel = r.AccessLevel,
            CreatedDate = DateTimeOffset.UtcNow,
        }).ToList();

        db.PrinterGroupAccesses.AddRange(newRules);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Set {Count} access rule(s) on printer group {GroupId}", newRules.Count, groupId);

        return await GetAccessRulesAsync(groupId, ct);
    }

    public async Task<bool> CanUserSubmitToGroupAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        List<PrinterGroupAccess> rules = await db.PrinterGroupAccesses
            .Where(a => a.PrinterGroupId == groupId)
            .ToListAsync(ct);

        // No rules → open to all (backward compatible)
        if (rules.Count == 0)
        {
            return true;
        }

        // Get the user's active role IDs
        List<Guid> userRoleIds = await db.UserRoles
            .Where(ur => ur.UserId == userId && ur.IsActive)
            .Select(ur => ur.RoleId)
            .ToListAsync(ct);

        // Check if any of the user's roles has Submit-level (or higher) access
        return rules.Any(r =>
            userRoleIds.Contains(r.RoleId) &&
            r.AccessLevel >= PrinterGroupAccessLevel.Submit);
    }

    private static PrinterGroupDto MapToDto(PrinterGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        CreatedDate = group.CreatedDate,
        UpdatedDate = group.UpdatedDate,
        PrinterCount = group.Printers.Count,
    };

    private static PrinterGroupDetailDto MapToDetailDto(PrinterGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Description = group.Description,
        CreatedDate = group.CreatedDate,
        UpdatedDate = group.UpdatedDate,
        Printers = group.Printers.Select(p => new PrinterGroupPrinterDto
        {
            Id = p.Id,
            Name = p.Name,
            Backend = p.Backend,
            IsAvailable = p.IsAvailable,
            InMaintenance = p.InMaintenance,
        }).ToList(),
    };
}
