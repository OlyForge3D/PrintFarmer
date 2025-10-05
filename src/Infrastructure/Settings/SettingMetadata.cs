using System.Collections.ObjectModel;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Describes metadata for a settings section, including key, class name, property info, and descriptions for dynamic UI and validation.
    /// </summary>
    public class SettingMetadata
    {
        /// <summary>
        /// The key name for the settings section.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// The class name of the settings section.
        /// </summary>
        public string ClassName { get; set; } = string.Empty;

        /// <summary>
        /// Optional display name for the settings section.
        /// </summary>
        public string? DisplayName { get; set; }

        /// <summary>
        /// Optional description of the settings section.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Optional icon for the settings section.
        /// </summary>
        public string? Icon { get; set; }

        /// <summary>
        /// Optional group name for UI grouping.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Optional order for UI sorting.
        /// </summary>
        public int? Order { get; set; }

        /// <summary>
        /// List of metadata for each property in the settings section.
        /// </summary>
        public ReadOnlyCollection<SettingPropertyMetadata> Properties { get; set; } = new ReadOnlyCollection<SettingPropertyMetadata>(Array.Empty<SettingPropertyMetadata>());
    }

    /// <summary>
    /// Describes metadata for a property in a settings section, including name, type, attributes, and display info.
    /// </summary>
    public class SettingPropertyMetadata
    {
        /// <summary>
        /// The name of the property.
        /// </summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>
        /// The type of the property (C# type name).
        /// </summary>
        public string Type { get; set; } = string.Empty;
        /// <summary>
        /// List of attribute names applied to the property.
        /// </summary>
        public ReadOnlyCollection<string> Attributes { get; set; } = new ReadOnlyCollection<string>(Array.Empty<string>());
        /// <summary>
        /// Optional display metadata for the property.
        /// </summary>
        public SettingPropertyDisplayMetadata? Display { get; set; }
    }
}
