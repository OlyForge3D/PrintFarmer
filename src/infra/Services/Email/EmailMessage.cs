namespace Farm.Infrastructure.Services.Email;

public record EmailMessage(
    string To,
    string Subject,
    string? PlainBody = null,
    string? HtmlBody = null,
    string TemplateKey = "",
    Dictionary<string, string>? Metadata = null)
{
    public string? CcAddress { get; init; }
}

public record EmailDispatchResult(
    bool Success,
    string? ProviderMessage = null,
    string? Error = null);
