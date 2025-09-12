namespace Farm.Web.Shared;

public class PasswordPolicyDto
{
    public int MinLength { get; set; } = 12;
    public bool RequireUppercase { get; set; } = false;
    public bool RequireLowercase { get; set; } = false;
    public bool RequireDigit { get; set; } = false;
    public bool RequireSymbol { get; set; } = false;
}

public class UpdatePasswordPolicyRequest
{
    public int? MinLength { get; set; }
    public bool? RequireUppercase { get; set; }
    public bool? RequireLowercase { get; set; }
    public bool? RequireDigit { get; set; }
    public bool? RequireSymbol { get; set; }
}