using System;

namespace Farm.Settings;

/// <summary>
/// Attribute to mark a class as a system setting (bootstrap, config-only).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemSettingAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
