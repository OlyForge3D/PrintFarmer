namespace Farm.Infrastructure.Services.Email;

public class EmailOptions
{
    public MailjetOptions? Mailjet { get; set; }
}

public class MailjetOptions
{
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
}
