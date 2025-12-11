namespace Farm.Web.Api.Domain;

public class PasswordPolicy
{
    public int Id { get; set; }
    // Relaxed default minimum length (was 12)
    public int MinLength { get; set; } = 8;
    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireDigit { get; set; }
    public bool RequireSymbol { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
