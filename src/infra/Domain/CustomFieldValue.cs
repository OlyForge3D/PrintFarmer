using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Stores the value of a custom field for a specific Printer or User entity.
/// </summary>
public class CustomFieldValue
{
    public Guid Id { get; set; }

    /// <summary>The field definition this value belongs to.</summary>
    public Guid DefinitionId { get; set; }

    public CustomFieldDefinition Definition { get; set; } = null!;

    /// <summary>The Printer.Id or User.Id that owns this value.</summary>
    public Guid EntityId { get; set; }

    /// <summary>Stored as a string; callers parse according to the definition's FieldType.</summary>
    [MaxLength(4000)]
    public string? Value { get; set; }

    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}
