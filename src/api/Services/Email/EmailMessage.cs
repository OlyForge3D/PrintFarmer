namespace Farm.Web.Api.Services.Email;

public sealed record EmailMessage(
    string To,
    string Subject,
    string? PlainBody = null,
    string? HtmlBody = null,
    string TemplateKey = "",
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record EmailDispatchResult(bool Success, string? ProviderMessage = null, string? Error = null);
