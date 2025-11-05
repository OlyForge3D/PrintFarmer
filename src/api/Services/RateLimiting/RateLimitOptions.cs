namespace Farm.Web.Api.Services.RateLimiting;

public class RateLimitOptions
{
    public PasswordResetRateLimitOptions PasswordReset { get; set; } = new();
    public EmailConfirmationRateLimitOptions EmailConfirmation { get; set; } = new();
    public SliceJobRateLimitOptions SliceJobs { get; set; } = new();
    public AuthenticationRateLimitOptions Authentication { get; set; } = new();
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

public class SliceJobRateLimitOptions
{
    public int MaxAttemptsPerHour { get; set; } = 20;
    public int MaxAttemptsPerDay { get; set; } = 200;
}

public class AuthenticationRateLimitOptions
{
    public int MaxLoginAttemptsPerMinute { get; set; } = 10;
    public int MaxRegisterAttemptsPerMinute { get; set; } = 10;
}
