namespace Farm.Infrastructure.Logging;

/// <summary>
/// Helpers for masking personally identifiable information (PII), such as email addresses,
/// before it is written to application logs. Application logs are persisted to the SystemLog
/// database table (see <see cref="SystemLogLoggingExtensions"/>) and may be queried, exported,
/// or viewed by operators other than the data subject, so raw PII should not flow into log
/// message arguments.
/// </summary>
public static class SensitiveDataMasking
{
    /// <summary>
    /// Masks an email address for logging, preserving only the first and last character of the
    /// local part and the full domain (e.g. "j***e@example.com"). This keeps logs useful for
    /// correlating support/audit events without exposing the full address.
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "unknown";
        }

        int atIndex = email.IndexOf('@', StringComparison.Ordinal);
        if (atIndex <= 0)
        {
            return MaskGeneric(email);
        }

        string local = email[..atIndex];
        string domain = email[(atIndex + 1)..];

        return local.Length <= 2
            ? $"***@{domain}"
            : $"{local[0]}***{local[^1]}@{domain}";
    }

    /// <summary>
    /// Masks a value only if it looks like an email address (contains '@'). Used for values that
    /// may be either an email address or a non-sensitive identifier such as an IP address, so
    /// non-email values are logged unmodified.
    /// </summary>
    public static string MaskIfEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Contains('@', StringComparison.Ordinal) ? MaskEmail(value) : value;
    }

    private static string MaskGeneric(string value)
    {
        return value.Length <= 2 ? "***" : $"{value[..2]}***";
    }
}
