namespace Farm.Web.Api.Domain;

public class PasswordPolicy
{
    public int Id { get; set; }
    public int MinLength { get; set; } = 12;
    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireDigit { get; set; }
    public bool RequireSymbol { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}