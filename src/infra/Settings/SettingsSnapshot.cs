namespace Farm.Infrastructure.Settings;

/// <summary>
/// A settings value with the mutation watermark captured before it was loaded.
/// </summary>
public sealed record SettingsSnapshot<T>(T Value, long? OriginWatermark)
    where T : class;
