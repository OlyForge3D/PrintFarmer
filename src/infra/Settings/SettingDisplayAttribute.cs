
using System;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Attribute for customizing the display and UI behavior of settings properties/classes in PrintFarmer.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class SettingDisplayAttribute : Attribute
    {
        /// <summary>
        /// Allowed values for select-type settings. Used to render dropdown/select lists in the UI.
        /// </summary>
        public object[]? AllowedValues { get; set; }

        /// <summary>
        /// Minimum allowed value for numeric settings. Used to constrain input in the UI.
        /// </summary>
        public double MinValue { get; set; } = -1;

        /// <summary>
        /// Maximum allowed value for numeric settings. Used to constrain input in the UI.
        /// </summary>
        public double MaxValue { get; set; } = -1;
        /// <summary>
        /// Display name for the setting property or class. Used as the label in the UI.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Description or help text for the setting. Shown as a tooltip or helper in the UI.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Optional icon name for the setting, used for visual grouping or emphasis.
        /// </summary>
        public string? Icon { get; set; }

        /// <summary>
        /// Logical group name for organizing settings in the UI.
        /// </summary>
        public string? Group { get; set; }

        /// <summary>
        /// Order index for sorting settings within a group or section.
        /// </summary>
        public int Order { get; set; } = 0;

        /// <summary>
        /// Input type for the setting property, determines the UI control (e.g. Text, Number, Select).
        /// </summary>
        public SettingInputType InputType { get; set; } = SettingInputType.Text;

        /// <summary>
        /// Indicates if the setting supports multiple values (e.g. array input).
        /// </summary>
        public bool IsMulti { get; set; } = false;

        /// <summary>
        /// Indicates if the setting property is required in the UI.
        /// </summary>
        public bool Required { get; set; } = false;

        /// <summary>
        /// Creates a new instance of SettingDisplayAttribute.
        /// </summary>
        public SettingDisplayAttribute() { }
    }
}
