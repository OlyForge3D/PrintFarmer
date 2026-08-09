namespace Farm.Infrastructure.Services.Email;

public class EmailOptions
{
    /// <summary>
    /// Master kill switch for email delivery. When <c>false</c>, the console
    /// (log-only) provider is always used regardless of <see cref="Provider"/>.
    /// Defaults to <c>true</c> so existing deployments that only set
    /// <c>Email:Provider</c>/<c>Email:Mailjet:*</c> keep working unchanged.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Selects the email delivery provider: <c>"mailjet"</c> for production
    /// delivery via the Mailjet API, or <c>"console"</c> (default) to log the
    /// payload only without sending.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>From-address used for outbound transactional email.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Display name paired with <see cref="FromAddress"/>.</summary>
    public string? FromName { get; set; }

    /// <summary>Public HTTPS origin used to build links embedded in emails (password reset, confirmation).</summary>
    public string? BaseUrl { get; set; }

    public MailjetOptions? Mailjet { get; set; }
}

public class MailjetOptions
{
    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    public string? FromEmail { get; set; }

    public string? FromName { get; set; }

    /// <summary>
    /// When <c>true</c>, requests are sent to Mailjet with <c>SandboxMode</c> enabled:
    /// the payload is validated but not actually delivered. Use for staging
    /// environments that want a real Mailjet round-trip without emailing users.
    /// </summary>
    public bool Sandbox { get; set; }
}
