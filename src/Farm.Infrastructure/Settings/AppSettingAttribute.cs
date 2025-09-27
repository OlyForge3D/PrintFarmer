using System;

namespace Farm.Infrastructure.Settings
{
    /// <summary>
    /// Attribute to mark a class as a settings class for runtime discovery.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class AppSettingAttribute : Attribute
    {
        public string Key { get; }
        public AppSettingAttribute(string key) => Key = key;
    }
}
