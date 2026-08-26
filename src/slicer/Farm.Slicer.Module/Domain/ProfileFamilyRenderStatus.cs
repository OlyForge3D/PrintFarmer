using System.Text.Json.Serialization;

namespace Farm.Slicer.Module.Domain;

/// <summary>
/// Describes whether a custom profile family has been rendered for its pinned OrcaSlicer version.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileFamilyRenderStatus
{
    /// <summary>The row is a stock model and does not require custom rendering.</summary>
    NotApplicable,

    /// <summary>The family is persisted but its derived worker bundle is not yet healthy.</summary>
    Pending,

    /// <summary>The derived worker bundle was written and its cache was invalidated successfully.</summary>
    Healthy,

    /// <summary>The most recent render attempt failed.</summary>
    Failed,

    /// <summary>The family was rendered for an older OrcaSlicer version.</summary>
    Stale
}
