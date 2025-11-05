namespace Farm.Web.Api.Services.Email;

public sealed class EmailOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "console"; // mailjet | console
    public string FromAddress { get; set; } = "noreply@example.com";
    public string FromName { get; set; } = "PrintFarmer";
    public string BaseUrl { get; set; } = "http://localhost:3000"; // used to build links
    public MailjetOptions? Mailjet { get; set; }
}

public sealed class MailjetOptions
{
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public bool Sandbox { get; set; } = true; // in sandbox mode Mailjet will not actually deliver
}
