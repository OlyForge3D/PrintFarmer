using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Per-user or per-group print quota with configurable limits and reset periods.
/// Tracks usage against cost, count, or weight limits.
/// </summary>
public class PrintQuota
{
    public Guid Id { get; set; }

    /// <summary>
    /// The user this quota applies to. Mutually exclusive with GroupName.
    /// </summary>
    public Guid? UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// Named group this quota applies to (e.g., "Students", "Faculty").
    /// Mutually exclusive with UserId.
    /// </summary>
    [MaxLength(200)]
    public string? GroupName { get; set; }

    /// <summary>
    /// What dimension this quota limits.
    /// </summary>
    public QuotaType QuotaType { get; set; }

    /// <summary>
    /// The maximum allowed amount for this quota period.
    /// Units depend on QuotaType: currency for Cost, count for Count, grams for Weight.
    /// </summary>
    public decimal LimitAmount { get; set; }

    /// <summary>
    /// How much of the limit has been consumed in the current period.
    /// </summary>
    public decimal UsedAmount { get; set; }

    /// <summary>
    /// How frequently the quota resets.
    /// </summary>
    public QuotaPeriodType PeriodType { get; set; }

    /// <summary>
    /// Start of the current quota period (UTC).
    /// </summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// When the current period ends and usage resets (UTC).
    /// Null for Manual period type (admin resets manually).
    /// </summary>
    public DateTime? ResetAt { get; set; }

    /// <summary>
    /// Whether this quota is actively enforced.
    /// </summary>
    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Dimension that a quota limits.
/// </summary>
public enum QuotaType
{
    /// <summary>Limits total print cost in the user's currency.</summary>
    Cost = 0,

    /// <summary>Limits total number of print jobs.</summary>
    Count = 1,

    /// <summary>Limits total filament weight in grams.</summary>
    Weight = 2
}

/// <summary>
/// How frequently quota usage resets.
/// </summary>
public enum QuotaPeriodType
{
    /// <summary>Resets every day at midnight UTC.</summary>
    Daily = 0,

    /// <summary>Resets every Monday at midnight UTC.</summary>
    Weekly = 1,

    /// <summary>Resets on the 1st of each month at midnight UTC.</summary>
    Monthly = 2,

    /// <summary>Resets every semester (~6 months).</summary>
    Semester = 3,

    /// <summary>Never auto-resets; admin must reset manually.</summary>
    Manual = 4
}
