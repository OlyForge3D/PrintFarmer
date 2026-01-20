namespace Farm.Web.Api.DTOs.Auth;

/// <summary>
/// DTO for password policy configuration
/// </summary>
public class PasswordPolicyDto
{
    // Default minimum length relaxed from 12 -> 8 to improve initial setup UX
    public int MinLength { get; set; } = 8;

    public bool RequireUppercase { get; set; }

    public bool RequireLowercase { get; set; }

    public bool RequireDigit { get; set; }

    public bool RequireSymbol { get; set; }
}

/// <summary>
/// Request DTO for updating password policy
/// </summary>
public class UpdatePasswordPolicyRequest
{
    public int? MinLength { get; set; }

    public bool? RequireUppercase { get; set; }

    public bool? RequireLowercase { get; set; }

    public bool? RequireDigit { get; set; }

    public bool? RequireSymbol { get; set; }
}
