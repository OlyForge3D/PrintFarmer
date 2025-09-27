using System.Collections.ObjectModel;

namespace Farm.Infrastructure.Settings
{
    public class SettingMetadata
    {
        public string Key { get; set; } = string.Empty;
        public string ClassName { get; set; } = string.Empty;
        public ReadOnlyCollection<SettingPropertyMetadata> Properties { get; set; } = new ReadOnlyCollection<SettingPropertyMetadata>(Array.Empty<SettingPropertyMetadata>());
    }

    public class SettingPropertyMetadata
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public ReadOnlyCollection<string> Attributes { get; set; } = new ReadOnlyCollection<string>(Array.Empty<string>());
    }
}
