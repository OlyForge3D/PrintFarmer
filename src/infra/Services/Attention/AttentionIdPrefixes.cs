namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Prefixes used to build stable attention item identifiers. The public id is
/// <c>{prefix}:{sourceId}</c>; clients and persisted snoozes reference this id.
/// </summary>
/// <remarks>
/// Prefixes are stable strings. Renaming a prefix breaks previously persisted snoozes,
/// so treat them as part of the public API.
/// </remarks>
public static class AttentionIdPrefixes
{
    /// <summary>Prefix for failure-detection incident items.</summary>
    public const string Failure = "failure";

    /// <summary>Prefix for maintenance-alert items.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>Prefix for offline-printer items.</summary>
    public const string Offline = "offline";

    /// <summary>Prefix for awaiting-harvest items.</summary>
    public const string Harvest = "harvest";

    /// <summary>Prefix reserved for filament-runout items (F4/#709).</summary>
    public const string Runout = "runout";

    /// <summary>Builds the canonical id string.</summary>
    public static string Build(string prefix, Guid sourceId) => $"{prefix}:{sourceId:D}";
}
