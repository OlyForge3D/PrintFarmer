using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.CustomFields;

/// <summary>
/// Business logic for managing custom field definitions and values.
/// </summary>
public interface ICustomFieldService
{
    /// <summary>Lists all definitions for a given entity type, ordered by SortOrder.</summary>
    Task<IReadOnlyList<CustomFieldDefinitionDto>> ListDefinitionsAsync(CustomFieldEntityType entityType, CancellationToken ct);

    /// <summary>Gets a single definition by ID.</summary>
    Task<CustomFieldDefinitionDto?> GetDefinitionByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Creates a new definition. FieldKey must be unique per EntityType.</summary>
    /// <exception cref="InvalidOperationException">FieldKey already exists for this entity type.</exception>
    Task<CustomFieldDefinitionDto> CreateDefinitionAsync(CreateCustomFieldDefinitionDto dto, CancellationToken ct);

    /// <summary>Updates a definition.</summary>
    /// <exception cref="KeyNotFoundException">Definition not found.</exception>
    /// <exception cref="InvalidOperationException">FieldKey conflict.</exception>
    Task<CustomFieldDefinitionDto> UpdateDefinitionAsync(Guid id, UpdateCustomFieldDefinitionDto dto, CancellationToken ct);

    /// <summary>Deletes a definition and all its values.</summary>
    /// <exception cref="KeyNotFoundException">Definition not found.</exception>
    Task DeleteDefinitionAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all field values for a single entity, including unset fields.</summary>
    Task<IReadOnlyList<CustomFieldValueDto>> GetValuesForEntityAsync(Guid entityId, CustomFieldEntityType entityType, CancellationToken ct);

    /// <summary>Bulk-upserts values for a single entity.</summary>
    Task SetValuesAsync(Guid entityId, CustomFieldEntityType entityType, Dictionary<Guid, string?> values, CancellationToken ct);

    /// <summary>Gets values for multiple entities (for list views).</summary>
    Task<Dictionary<Guid, IReadOnlyList<CustomFieldValueDto>>> BulkGetValuesAsync(IEnumerable<Guid> entityIds, CustomFieldEntityType entityType, CancellationToken ct);
}
