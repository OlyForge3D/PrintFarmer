using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.BedTypes;

/// <summary>
/// Service layer for bed type operations with business rule enforcement.
/// </summary>
public class BedTypeService(
    AppDbContext db,
    ILogger<BedTypeService> logger) : IBedTypeService
{
    public async Task<IReadOnlyList<BedTypeDto>> ListAllAsync(CancellationToken ct)
    {
        List<BedType> bedTypes = await db.BedTypes
            .Include(b => b.Printers)
            .OrderBy(b => b.Name)
            .ToListAsync(ct);

        return bedTypes.Select(MapToDto).ToList();
    }

    public async Task<BedTypeDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        BedType? bedType = await db.BedTypes
            .Include(b => b.Printers)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        return bedType is null ? null : MapToDto(bedType);
    }

    public async Task<BedTypeDto> CreateAsync(CreateBedTypeDto dto, CancellationToken ct)
    {
        string trimmedName = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Bed type name is required.");
        }

        bool exists = await db.BedTypes.AnyAsync(b => b.Name == trimmedName, ct);
        if (exists)
        {
            throw new InvalidOperationException($"A bed type named '{trimmedName}' already exists.");
        }

        var bedType = new BedType
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            Description = dto.Description?.Trim(),
            Color = dto.Color?.Trim(),
            IsSystem = false,
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow,
        };

        db.BedTypes.Add(bedType);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created bed type '{Name}' ({Id})", bedType.Name, bedType.Id);
        return MapToDto(bedType);
    }

    public async Task<BedTypeDto> UpdateAsync(Guid id, UpdateBedTypeDto dto, CancellationToken ct)
    {
        BedType? bedType = await db.BedTypes
            .Include(b => b.Printers)
            .FirstOrDefaultAsync(b => b.Id == id, ct);

        if (bedType is null)
        {
            throw new KeyNotFoundException($"Bed type {id} not found.");
        }

        string trimmedName = dto.Name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Bed type name is required.");
        }

        if (!string.Equals(bedType.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
        {
            bool conflict = await db.BedTypes.AnyAsync(b => b.Name == trimmedName && b.Id != id, ct);
            if (conflict)
            {
                throw new InvalidOperationException($"A bed type named '{trimmedName}' already exists.");
            }
        }

        bedType.Name = trimmedName;
        bedType.Description = dto.Description?.Trim();
        bedType.Color = dto.Color?.Trim();
        bedType.UpdatedDate = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Updated bed type '{Name}' ({Id})", bedType.Name, bedType.Id);
        return MapToDto(bedType);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        BedType? bedType = await db.BedTypes.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (bedType is null)
        {
            throw new KeyNotFoundException($"Bed type {id} not found.");
        }

        if (bedType.IsSystem)
        {
            throw new InvalidOperationException($"Cannot delete system bed type '{bedType.Name}'.");
        }

        db.BedTypes.Remove(bedType);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Deleted bed type '{Name}' ({Id})", bedType.Name, bedType.Id);
    }

    private static BedTypeDto MapToDto(BedType bedType) => new()
    {
        Id = bedType.Id,
        Name = bedType.Name,
        Description = bedType.Description,
        IsSystem = bedType.IsSystem,
        Color = bedType.Color,
        CreatedDate = bedType.CreatedDate,
        UpdatedDate = bedType.UpdatedDate,
        PrinterCount = bedType.Printers.Count,
    };
}
