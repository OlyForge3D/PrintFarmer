using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Maps a role to an access level on a printer group.
/// When no access rules exist for a group, it remains open to all users (backward compatible).
/// </summary>
public class PrinterGroupAccess
{
    public Guid Id { get; set; }

    public Guid PrinterGroupId { get; set; }

    public PrinterGroup PrinterGroup { get; set; } = null!;

    public Guid RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public PrinterGroupAccessLevel AccessLevel { get; set; } = PrinterGroupAccessLevel.Submit;

    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Access levels for printer group operations.
/// Higher numeric values imply all lower-level permissions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrinterGroupAccessLevel
{
    View = 0,
    Submit = 1,
    Manage = 2
}
