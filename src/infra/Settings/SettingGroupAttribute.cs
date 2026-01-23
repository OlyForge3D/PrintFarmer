using System;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Attribute for defining settings group metadata. Apply to any settings class to define
/// group-level properties like display order, name, description, and icon.
/// If multiple settings classes define the same group, the one with the lowest GroupOrder wins.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SettingGroupAttribute : Attribute
{
    /// <summary>
    /// The unique identifier for the group. This should match the Group property on SettingDisplayAttribute.
    /// </summary>
    public string GroupKey { get; }

    /// <summary>
    /// Display name for the group in the UI sidebar.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Description of the group, shown as a tooltip or header in the UI.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Optional icon name for the group.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Order index for sorting groups in the sidebar. Lower values appear first.
    /// </summary>
    public int Order { get; set; } = 100;

    /// <summary>
    /// Creates a new instance of SettingGroupAttribute.
    /// </summary>
    /// <param name="groupKey">The unique identifier for the group.</param>
    public SettingGroupAttribute(string groupKey)
    {
        GroupKey = groupKey ?? throw new ArgumentNullException(nameof(groupKey));
    }
}
