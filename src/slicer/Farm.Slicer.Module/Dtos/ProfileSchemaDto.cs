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

    /// <summary>
    /// Optional lower bound on OrcaSlicer engine version at which this field becomes available.
    /// Null = no lower bound. Example: "2.4.0" means the field only appears when the caller
    /// requests schema for engineVersion &gt;= 2.4.0 (or requests without engineVersion filter).
    /// Applied by <see cref="Services.ProfileSchemaProvider"/> via <see cref="System.Version"/> comparison.
    /// </summary>
    public string? MinEngineVersion { get; init; }

    /// <summary>
    /// Optional upper bound on OrcaSlicer engine version at which this field is still available.
    /// Null = no upper bound. Example: "2.3.99" means the field is retired for engineVersion &gt;= 2.4.0.
    /// </summary>
    public string? MaxEngineVersion { get; init; }

    /// <summary>
    /// If set, this field was renamed in a specific engine version. When the caller requests
    /// schema for an engine version older than <see cref="RenamedInVersion"/>, the field is
    /// emitted with <see cref="RenamedFromKey"/> as its <see cref="Key"/> instead. This lets
    /// the frontend render the correct key for a pinned engine version without leaking the
    /// post-rename key into older-engine payloads.
    /// </summary>
    public string? RenamedFromKey { get; init; }

    /// <summary>
    /// The engine version in which this field's key changed from <see cref="RenamedFromKey"/>
    /// to <see cref="Key"/>. Ignored when <see cref="RenamedFromKey"/> is null.
    /// </summary>
    public string? RenamedInVersion { get; init; }
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
