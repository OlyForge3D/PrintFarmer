using System;

namespace Farm.Settings;

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
    /// Name of a boolean property in the same section that gates <see cref="Required"/>.
    /// When set, the property is only required while that property is <c>true</c>.
    /// <para>
    /// This exists because settings sections routinely enforce invariants in
    /// <c>Validate()</c> that the UI cannot see — a subnet list that must be
    /// non-empty whenever discovery is enabled, for example. Without this the
    /// requirement is invisible until a save fails, which is the wrong moment
    /// to learn about it.
    /// </para>
    /// <para>
    /// Use the serialized (JSON) property name, since that is the name the
    /// client sees.
    /// </para>
    /// </summary>
    public string? RequiredWhen { get; set; }

    /// <summary>
    /// Unit of measure for the value, rendered as an adornment beside the
    /// control rather than as part of the label.
    /// <para>
    /// Units used to be written into <see cref="Name"/> — "Runout Warning Lead
    /// Time (minutes)". That is the single largest contributor to label width:
    /// nine of the ten labels that wrap in the settings UI do so only because
    /// of their parenthetical. Widening the label track to fit them cost every
    /// row in the application 96px and pushed the two-column layout out by
    /// ~550px of viewport (#1030), so the unit moves to where it belongs.
    /// </para>
    /// <para>
    /// Write the bare unit exactly as it should read beside a number, with no
    /// parentheses and no Title Case: <c>Unit = "minutes"</c>, not
    /// <c>"(minutes)"</c> or <c>"Minutes"</c>. Standard abbreviations keep
    /// their own casing — <c>"MB"</c>, <c>"ms"</c>, <c>"kg"</c> — since that is
    /// how they read next to a number.
    /// </para>
    /// <para>
    /// The property also carries reference frames that are not strictly units,
    /// such as <c>"UTC"</c> beside a time-of-day field. They render and read
    /// identically to a unit, so they use the same slot rather than earning a
    /// second one.
    /// </para>
    /// </summary>
    public string? Unit { get; set; }
}
