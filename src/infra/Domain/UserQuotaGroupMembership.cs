using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Associates a user with a named quota group for group-level quota enforcement.
/// </summary>
public class UserQuotaGroupMembership
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>
    /// The quota group name (matches <see cref="PrintQuota.GroupName"/>).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string GroupName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
