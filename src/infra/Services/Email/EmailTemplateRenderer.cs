using System.Text;

namespace Farm.Infrastructure.Services.Email;

public interface IEmailTemplateRenderer
{
    (string Subject, string PlainBody, string HtmlBody) Render(string templateKey, IReadOnlyDictionary<string, string> model);
}

public sealed class EmailTemplateRenderer : IEmailTemplateRenderer
{
    // For MVP keep templates inline. Future: move to embedded resources or RazorLight.
    private static readonly Dictionary<string, (string Subject, string Plain, string Html)> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PasswordReset"] = (
            "Reset your PrintFarmer password",
            "You requested a password reset.\n\nIf you did not request this, ignore this message.\n\nReset Link: {{ResetLink}}\nThis link expires in {{ExpirationMinutes}} minutes.",
            "<p>You requested a password reset.</p><p>If you did not request this, you can safely ignore this email.</p><p><a href=\"{{ResetLink}}\" style=\"background:#2563eb;color:#fff;padding:10px 16px;text-decoration:none;border-radius:4px;display:inline-block\">Reset Password</a></p><p>This link expires in <strong>{{ExpirationMinutes}}</strong> minutes.</p>"
        ),
        ["EmailConfirmation"] = (
            "Confirm your PrintFarmer email address",
            "Welcome to PrintFarmer!\n\nPlease confirm your email address by clicking the link below:\n\n{{ConfirmationLink}}\n\nThis link expires in {{ExpirationHours}} hours.\n\nIf you did not create an account, you can safely ignore this email.",
            "<p>Welcome to <strong>PrintFarmer</strong>!</p><p>Please confirm your email address to activate your account:</p><p><a href=\"{{ConfirmationLink}}\" style=\"background:#16a34a;color:#fff;padding:10px 16px;text-decoration:none;border-radius:4px;display:inline-block\">Confirm Email Address</a></p><p>This link expires in <strong>{{ExpirationHours}}</strong> hours.</p><p style=\"color:#666;font-size:0.9em\">If you did not create an account, you can safely ignore this email.</p>"
        )
    };

    public (string Subject, string PlainBody, string HtmlBody) Render(string templateKey, IReadOnlyDictionary<string, string> model)
    {
        if (!Templates.TryGetValue(templateKey, out (string Subject, string Plain, string Html) tpl))
        {
            throw new KeyNotFoundException($"Email template '{templateKey}' not found");
        }

        string subject = InterpolateTemplate(tpl.Subject, model);
        string plainBody = InterpolateTemplate(tpl.Plain, model);
        string htmlBody = InterpolateTemplate(tpl.Html, model);

        return (subject, plainBody, htmlBody);
    }

    private static string InterpolateTemplate(string template, IReadOnlyDictionary<string, string> model)
    {
        var result = new StringBuilder(template);
        foreach ((string key, string value) in model)
        {
            result.Replace($"{{{{{key}}}}}", value);
        }

        return result.ToString();
    }
}
