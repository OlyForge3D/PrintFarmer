using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

public class RolePermission
{
    public Guid Id { get; set; }

    public Guid RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public Guid ResourceId { get; set; }

    public Resource Resource { get; set; } = null!;

    public Guid ActionId { get; set; }

    public UserAction Action { get; set; } = null!;

    public bool Granted { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
