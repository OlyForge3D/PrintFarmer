namespace Farm.Infrastructure.Services.Email;

/// <summary>
/// Service for sending emails including transactional and notification emails.
/// Supports password reset, email confirmation, and custom email messages.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends an email message.
    /// </summary>
    /// <param name="message">The email message to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or failure with error details.</returns>
    Task<EmailDispatchResult> SendAsync(EmailMessage message, CancellationToken ct = default);

    /// <summary>
    /// Sends a password reset email with the provided reset link.
    /// </summary>
    /// <param name="email">The recipient's email address.</param>
    /// <param name="resetLink">The password reset link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if email sent successfully.</returns>
    Task<bool> SendPasswordResetAsync(string email, string resetLink, CancellationToken ct = default);

    /// <summary>
    /// Sends an email confirmation with the provided confirmation link.
    /// </summary>
    /// <param name="email">The recipient's email address.</param>
    /// <param name="confirmationLink">The email confirmation link.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if email sent successfully.</returns>
    Task<bool> SendEmailConfirmationAsync(string email, string confirmationLink, CancellationToken ct = default);
}
