namespace Farm.Infrastructure.Services.Email;

public interface IEmailService
{
    Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default);

    Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default);

    Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default);
}
