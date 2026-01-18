namespace Farm.Infrastructure.Settings;

/// <summary>
/// Marker interface for settings classes.
/// </summary>
public interface ISystemSetting
{
    /// <summary>
    /// The unique key or section name for this settings class.
    /// </summary>
    static abstract string SectionKey { get; }
}
