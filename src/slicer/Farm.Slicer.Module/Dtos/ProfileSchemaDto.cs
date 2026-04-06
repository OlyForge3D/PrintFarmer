namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Metadata for a single field in a slicer profile, powering schema-driven settings editors.
/// </summary>
public sealed class ProfileFieldMetadata
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required string FieldType { get; init; }

    public required string Category { get; init; }

    public string? Description { get; init; }

    public object? DefaultValue { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public double? Step { get; init; }

    public string? Unit { get; init; }

    public List<EnumOptionDto>? Options { get; init; }

    public bool IsAdvanced { get; init; }
}

/// <summary>
/// A selectable option for enum/select-type fields.
/// </summary>
public sealed class EnumOptionDto
{
    public required string Value { get; init; }

    public required string Label { get; init; }
}

/// <summary>
/// Complete schema for a profile type, including field metadata and category ordering.
/// </summary>
public sealed class ProfileTypeSchemaDto
{
    public required string ProfileType { get; init; }

    public required List<string> Categories { get; init; }

    public required List<ProfileFieldMetadata> Fields { get; init; }
}

/// <summary>
/// Combined response containing schemas for all profile types.
/// </summary>
public sealed class ProfileSchemasResponseDto
{
    public required ProfileTypeSchemaDto Process { get; init; }

    public required ProfileTypeSchemaDto Machine { get; init; }

    public required ProfileTypeSchemaDto Filament { get; init; }
}
