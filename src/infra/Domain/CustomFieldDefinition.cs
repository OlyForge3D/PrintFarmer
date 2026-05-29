using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Defines a custom metadata field that can be attached to Printer or User entities.
/// </summary>
public class CustomFieldDefinition
{
    public Guid Id { get; set; }

    /// <summary>Whether this field applies to Printer or User entities.</summary>
    public CustomFieldEntityType EntityType { get; set; }

    /// <summary>Display name shown in the UI.</summary>
    [Required]
    [MaxLength(200)]
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Unique programmatic key per entity type (kebab-case).</summary>
    [Required]
    [MaxLength(100)]
    public string FieldKey { get; set; } = string.Empty;

    /// <summary>Data type of the field value.</summary>
    public CustomFieldType FieldType { get; set; }

    /// <summary>JSON array of allowed values for Select fields, e.g. ["Option A","Option B"].</summary>
    [MaxLength(4000)]
    public string? Options { get; set; }

    /// <summary>Whether a value is required when editing the parent entity.</summary>
    public bool IsRequired { get; set; }

    /// <summary>Display order among fields of the same entity type.</summary>
    public int SortOrder { get; set; }

    /// <summary>Optional help text shown below the field in the UI.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Default value applied when a new entity is created.</summary>
    [MaxLength(1000)]
    public string? DefaultValue { get; set; }

    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Values stored against individual entities.</summary>
    public ICollection<CustomFieldValue> Values { get; set; } = new List<CustomFieldValue>();
}

/// <summary>Target entity type for a custom field definition.</summary>
public enum CustomFieldEntityType
{
    Printer = 0,
    User = 1,
}

/// <summary>Supported data types for custom field values.</summary>
public enum CustomFieldType
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    Select = 4,
}
