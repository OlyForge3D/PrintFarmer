using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

[SuppressMessage("Naming", "CA1724:Type names should not match namespace", Justification = "Renamed infra domain type to PasswordPolicyEntity to avoid CA1724 conflicts with API domain type.")]
public class PasswordPolicyEntity
{
    public int Id { get; set; }

    public int MinLength { get; set; } = 8;

    public bool RequireUppercase { get; set; }

    public bool RequireLowercase { get; set; }

    public bool RequireDigit { get; set; }

    public bool RequireSymbol { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
