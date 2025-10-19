namespace Farm.Web.Api.Services.RateLimiting;

public class RateLimitOptions
{
    public PasswordResetRateLimitOptions PasswordReset { get; set; } = new();
    public EmailConfirmationRateLimitOptions EmailConfirmation { get; set; } = new();
}

public class PasswordResetRateLimitOptions
{
    public int MaxAttemptsPerHour { get; set; } = 3;
    public int MaxAttemptsPerDay { get; set; } = 10;
}

public class EmailConfirmationRateLimitOptions
{
    public int MaxAttemptsPerHour { get; set; } = 5;
    public int MaxAttemptsPerDay { get; set; } = 20;
}
