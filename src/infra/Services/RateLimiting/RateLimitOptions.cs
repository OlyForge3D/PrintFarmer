namespace Farm.Infrastructure.Services.RateLimiting;

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

    /// <summary>
    /// Maximum Desktop API-key exchange attempts allowed per minute per IP address.
    /// Kept tighter than login/register since a legitimate desktop client only needs
    /// to exchange a key roughly once per token lifetime, while a lower ceiling makes
    /// brute-forcing keys or enumerating valid ones materially slower.
    /// </summary>
    public int MaxApiKeyExchangeAttemptsPerMinute { get; set; } = 5;
}
