namespace Farm.Infrastructure.Settings;

/// <summary>
/// Metadata for a settings group, used for UI rendering and organization.
/// </summary>
public class SettingGroupMetadata
{
    /// <summary>
    /// The unique identifier for the group.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Display name for the group in the UI sidebar.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Description of the group, shown as a tooltip or header in the UI.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Optional icon name for the group.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Order index for sorting groups in the sidebar. Lower values appear first.
    /// </summary>
    public int Order { get; init; } = 100;
}
