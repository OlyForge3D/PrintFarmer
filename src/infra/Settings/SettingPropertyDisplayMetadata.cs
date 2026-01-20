namespace Farm.Infrastructure.Settings;

public class SettingPropertyDisplayMetadata
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Icon { get; set; }

    public string? Group { get; set; }

    public int Order { get; set; }

    public SettingInputType InputType { get; set; } = SettingInputType.Text;

    public bool IsMulti { get; set; }

    public bool Required { get; set; }

    // Allowed values for select-type settings
    public object[]? AllowedValues { get; set; }

    // Minimum allowed value for numeric settings
    public double? MinValue { get; set; }

    // Maximum allowed value for numeric settings
    public double? MaxValue { get; set; }
}
