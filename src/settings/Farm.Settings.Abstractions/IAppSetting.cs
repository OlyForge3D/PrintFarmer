namespace Farm.Settings;

/// <summary>
/// Marker interface for AppSettings (runtime/configurable, persisted in DB).
/// </summary>
public interface IAppSetting
{
    static abstract string SectionKey { get; }
}
