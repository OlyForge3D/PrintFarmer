using System;

namespace Farm.Infrastructure.Settings;

/// <summary>
/// Attribute to mark a class as a system setting (bootstrap, config-only).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemSettingAttribute : Attribute
{
    public string Key { get; }
    public SystemSettingAttribute(string key) => Key = key;
}
