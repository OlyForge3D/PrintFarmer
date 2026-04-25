using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.CustomFields;

/// <summary>
/// Service for managing custom field definitions and per-entity values.
/// </summary>
public class CustomFieldService(
    AppDbContext db,
    ILogger<CustomFieldService> logger) : ICustomFieldService
{
    public async Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(
        CustomFieldEntityType entityType, CancellationToken ct)
    {
        List<CustomFieldDefinition> defs = await db.CustomFieldDefinitions
            .Where(d => d.EntityType == entityType)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.FieldName)
            .ToListAsync(ct);

        return defs.Select(MapDefinitionToDto).ToList();
    }

    public async Task<CustomFieldDefinitionDto?> GetDefinitionByIdAsync(Guid id, CancellationToken ct)
    {
        CustomFieldDefinition? def = await db.CustomFieldDefinitions
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        return def is null ? null : MapDefinitionToDto(def);
    }

    public async Task<CustomFieldDefinitionDto> CreateDefinitionAsync(
        CreateCustomFieldDefinitionDto dto, CancellationToken ct)
    {
        string trimmedName = dto.FieldName.Trim();
        string trimmedKey = dto.FieldKey.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Field name is required.");
        }

        if (string.IsNullOrWhiteSpace(trimmedKey))
        {
            throw new InvalidOperationException("Field key is required.");
        }

        bool exists = await db.CustomFieldDefinitions
            .AnyAsync(d => d.EntityType == dto.EntityType && d.FieldKey == trimmedKey, ct);
        if (exists)
        {
            throw new InvalidOperationException(
                $"A custom field with key '{trimmedKey}' already exists for {dto.EntityType}.");
        }

        var definition = new CustomFieldDefinition
        {
            Id = Guid.NewGuid(),
            EntityType = dto.EntityType,
            FieldName = trimmedName,
            FieldKey = trimmedKey,
            FieldType = dto.FieldType,
            Options = dto.Options?.Trim(),
            IsRequired = dto.IsRequired,
            SortOrder = dto.SortOrder,
            Description = dto.Description?.Trim(),
            DefaultValue = dto.DefaultValue?.Trim(),
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow,
        };

        db.CustomFieldDefinitions.Add(definition);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Created custom field definition '{FieldName}' ({Id}) for {EntityType}",
            definition.FieldName, definition.Id, definition.EntityType);

        return MapDefinitionToDto(definition);
    }

    public async Task<CustomFieldDefinitionDto> UpdateDefinitionAsync(
        Guid id, UpdateCustomFieldDefinitionDto dto, CancellationToken ct)
    {
        CustomFieldDefinition? definition = await db.CustomFieldDefinitions
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (definition is null)
        {
            throw new KeyNotFoundException($"Custom field definition {id} not found.");
        }

        string trimmedName = dto.FieldName.Trim();
        string trimmedKey = dto.FieldKey.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("Field name is required.");
        }

        if (string.IsNullOrWhiteSpace(trimmedKey))
        {
            throw new InvalidOperationException("Field key is required.");
        }

        if (!string.Equals(definition.FieldKey, trimmedKey, StringComparison.OrdinalIgnoreCase))
        {
            bool conflict = await db.CustomFieldDefinitions
                .AnyAsync(d => d.EntityType == definition.EntityType && d.FieldKey == trimmedKey && d.Id != id, ct);
            if (conflict)
            {
                throw new InvalidOperationException(
                    $"A custom field with key '{trimmedKey}' already exists for {definition.EntityType}.");
            }
        }

        definition.FieldName = trimmedName;
        definition.FieldKey = trimmedKey;
        definition.FieldType = dto.FieldType;
        definition.Options = dto.Options?.Trim();
        definition.IsRequired = dto.IsRequired;
        definition.SortOrder = dto.SortOrder;
        definition.Description = dto.Description?.Trim();
        definition.DefaultValue = dto.DefaultValue?.Trim();
        definition.UpdatedDate = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Updated custom field definition '{FieldName}' ({Id})",
            definition.FieldName, definition.Id);

        return MapDefinitionToDto(definition);
    }

    public async Task DeleteDefinitionAsync(Guid id, CancellationToken ct)
    {
        CustomFieldDefinition? definition = await db.CustomFieldDefinitions
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (definition is null)
        {
            throw new KeyNotFoundException($"Custom field definition {id} not found.");
        }

        db.CustomFieldDefinitions.Remove(definition);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Deleted custom field definition '{FieldName}' ({Id})",
            definition.FieldName, definition.Id);
    }

    public async Task<IReadOnlyList<CustomFieldValueDto>> GetValuesForEntityAsync(
        Guid entityId, CustomFieldEntityType entityType, CancellationToken ct)
    {
        List<CustomFieldDefinition> definitions = await db.CustomFieldDefinitions
            .Where(d => d.EntityType == entityType)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.FieldName)
            .ToListAsync(ct);

        List<CustomFieldValue> values = await db.CustomFieldValues
            .Where(v => v.EntityId == entityId && v.Definition.EntityType == entityType)
            .ToListAsync(ct);

        Dictionary<Guid, CustomFieldValue> valueMap = values.ToDictionary(v => v.DefinitionId);

        return definitions.Select(d => new CustomFieldValueDto
        {
            DefinitionId = d.Id,
            FieldName = d.FieldName,
            FieldKey = d.FieldKey,
            FieldType = d.FieldType,
            Value = valueMap.TryGetValue(d.Id, out CustomFieldValue? v) ? v.Value : null,
            Options = d.Options,
            IsRequired = d.IsRequired,
        }).ToList();
    }

    public async Task SetValuesAsync(
        Guid entityId, CustomFieldEntityType entityType, Dictionary<Guid, string?> values, CancellationToken ct)
    {
        HashSet<Guid> definitionIds = values.Keys.ToHashSet();

        // Validate all definition IDs belong to the specified entity type
        HashSet<Guid> validIds = (await db.CustomFieldDefinitions
            .Where(d => definitionIds.Contains(d.Id) && d.EntityType == entityType)
            .Select(d => d.Id)
            .ToListAsync(ct)).ToHashSet();

        HashSet<Guid> invalidIds = definitionIds.Except(validIds).ToHashSet();
        if (invalidIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Definition IDs [{string.Join(", ", invalidIds)}] do not belong to entity type {entityType}");
        }

        List<CustomFieldValue> existing = await db.CustomFieldValues
            .Where(v => v.EntityId == entityId && definitionIds.Contains(v.DefinitionId))
            .ToListAsync(ct);

        Dictionary<Guid, CustomFieldValue> existingMap = existing.ToDictionary(v => v.DefinitionId);

        foreach (KeyValuePair<Guid, string?> entry in values)
        {
            if (existingMap.TryGetValue(entry.Key, out CustomFieldValue? existingValue))
            {
                existingValue.Value = entry.Value;
                existingValue.UpdatedDate = DateTimeOffset.UtcNow;
            }
            else
            {
                db.CustomFieldValues.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    DefinitionId = entry.Key,
                    EntityId = entityId,
                    Value = entry.Value,
                    CreatedDate = DateTimeOffset.UtcNow,
                    UpdatedDate = DateTimeOffset.UtcNow,
                });
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Set {Count} custom field values for {EntityType} {EntityId}",
            values.Count, entityType, entityId);
    }

    public async Task<Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>>> BulkGetValuesAsync(
        IEnumerable<Guid> entityIds, CustomFieldEntityType entityType, CancellationToken ct)
    {
        List<Guid> ids = entityIds.ToList();

        List<CustomFieldDefinition> definitions = await db.CustomFieldDefinitions
            .Where(d => d.EntityType == entityType)
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.FieldName)
            .ToListAsync(ct);

        List<CustomFieldValue> allValues = await db.CustomFieldValues
            .Where(v => ids.Contains(v.EntityId) && v.Definition.EntityType == entityType)
            .ToListAsync(ct);

        ILookup<Guid, CustomFieldValue> valueLookup = allValues.ToLookup(v => v.EntityId);

        var result = new Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>>();

        foreach (Guid entityId in ids)
        {
            Dictionary<Guid, CustomFieldValue> valueMap = valueLookup[entityId]
                .ToDictionary(v => v.DefinitionId);

            result[entityId] = definitions.Select(d => new CustomFieldValueDto
            {
                DefinitionId = d.Id,
                FieldName = d.FieldName,
                FieldKey = d.FieldKey,
                FieldType = d.FieldType,
                Value = valueMap.TryGetValue(d.Id, out CustomFieldValue? v) ? v.Value : null,
                Options = d.Options,
                IsRequired = d.IsRequired,
            }).ToList();
        }

        return result;
    }

    private static CustomFieldDefinitionDto MapDefinitionToDto(CustomFieldDefinition def) => new()
    {
        Id = def.Id,
        EntityType = def.EntityType,
        FieldName = def.FieldName,
        FieldKey = def.FieldKey,
        FieldType = def.FieldType,
        Options = def.Options,
        IsRequired = def.IsRequired,
        SortOrder = def.SortOrder,
        Description = def.Description,
        DefaultValue = def.DefaultValue,
        CreatedDate = def.CreatedDate,
        UpdatedDate = def.UpdatedDate,
    };
}
