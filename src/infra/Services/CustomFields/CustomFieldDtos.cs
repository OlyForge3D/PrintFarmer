using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.CustomFields;

/// <summary>DTOs for the Custom Fields API.</summary>

public record CustomFieldDefinitionDto
{
    public Guid Id { get; init; }

    public CustomFieldEntityType EntityType { get; init; }

    public string FieldName { get; init; } = string.Empty;

    public string FieldKey { get; init; } = string.Empty;

    public CustomFieldType FieldType { get; init; }

    public string? Options { get; init; }

    public bool IsRequired { get; init; }

    public int SortOrder { get; init; }

    public string? Description { get; init; }

    public string? DefaultValue { get; init; }

    public DateTimeOffset CreatedDate { get; init; }

    public DateTimeOffset UpdatedDate { get; init; }
}

public record CreateCustomFieldDefinitionDto
{
    public CustomFieldEntityType EntityType { get; init; }

    public string FieldName { get; init; } = string.Empty;

    public string FieldKey { get; init; } = string.Empty;

    public CustomFieldType FieldType { get; init; }

    public string? Options { get; init; }

    public bool IsRequired { get; init; }

    public int SortOrder { get; init; }

    public string? Description { get; init; }

    public string? DefaultValue { get; init; }
}

public record UpdateCustomFieldDefinitionDto
{
    public string FieldName { get; init; } = string.Empty;

    public string FieldKey { get; init; } = string.Empty;

    public CustomFieldType FieldType { get; init; }

    public string? Options { get; init; }

    public bool IsRequired { get; init; }

    public int SortOrder { get; init; }

    public string? Description { get; init; }

    public string? DefaultValue { get; init; }
}

public record CustomFieldValueDto
{
    public Guid DefinitionId { get; init; }

    public string FieldName { get; init; } = string.Empty;

    public string FieldKey { get; init; } = string.Empty;

    public CustomFieldType FieldType { get; init; }

    public string? Value { get; init; }

    public string? Options { get; init; }

    public bool IsRequired { get; init; }
}

public record SetCustomFieldValuesRequest
{
    public Dictionary<Guid, string?> Values { get; init; } = new();
}

public record BulkGetCustomFieldValuesRequest
{
    public CustomFieldEntityType EntityType { get; init; }

    public List<Guid> EntityIds { get; init; } = new();
}
