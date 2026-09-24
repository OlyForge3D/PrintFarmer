namespace Farm.Infrastructure.Settings;

/// <summary>A settings value and the revision captured with that value.</summary>
public sealed record SettingsSectionSnapshot(object Value, string RowVersion)
{
    /// <summary>Token for a section that has not yet been persisted.</summary>
    public const string AbsentRowVersion = "absent";
}
