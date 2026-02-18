using System;

namespace Farm.Settings;

/// <summary>
/// Attribute to mark a class as an application setting (runtime, persisted in DB).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AppSettingAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
