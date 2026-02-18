namespace Farm.Settings;

/// <summary>
/// Specifies the type of input control to use for a settings property in the UI.
/// </summary>
public enum SettingInputType
{
    /// <summary>Single-line text input.</summary>
    Text,

    /// <summary>Multi-line text area for longer content.</summary>
    TextArea,

    /// <summary>Numeric input (integer or float).</summary>
    Number,

    /// <summary>Boolean input (checkbox or switch).</summary>
    Boolean,

    /// <summary>Password input (masked text).</summary>
    Password,

    /// <summary>Dropdown/select input.</summary>
    Select,

    /// <summary>Multi-select input (multiple options).</summary>
    MultiSelect,

    /// <summary>Array/multi-value input (e.g., list of strings).</summary>
    Array,

    /// <summary>IP address input.</summary>
    IpAddress,

    /// <summary>Subnet input.</summary>
    Subnet,

    /// <summary>Hostname input.</summary>
    Hostname,

    /// <summary>URL input.</summary>
    Url,

    /// <summary>Color picker input.</summary>
    Color,

    /// <summary>File picker input.</summary>
    File,

    /// <summary>Directory picker input.</summary>
    Directory,

    /// <summary>Custom input type (for advanced UI).</summary>
    Custom
}
