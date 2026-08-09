using System.Text;

namespace Farm.Infrastructure.Logging;

/// <summary>
/// Sanitizes user- or device-controlled strings before they are written to a log sink.
/// Prevents log forging (CodeQL cs/log-forging): an attacker who controls a logged value
/// (e.g. an HTTP header, a printer-supplied filename, a request parameter) could otherwise
/// embed CR/LF sequences to inject fake log lines or other control characters to corrupt
/// log output.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Returns a copy of <paramref name="value"/> that is safe to interpolate into a log
    /// message: carriage return and line feed characters are replaced with the literal
    /// escape sequences <c>\r</c> and <c>\n</c> so they cannot start a new log line, and all
    /// other ASCII control characters (e.g. tabs, null, escape) are stripped. Printable
    /// characters, including non-ASCII text, are left unchanged.
    /// </summary>
    /// <param name="value">The value to sanitize. May be null or empty.</param>
    /// <returns>
    /// The original value if it contains no control characters (including <see langword="null"/>
    /// or empty strings), otherwise a sanitized copy safe for logging.
    /// </returns>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int firstControlIndex = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                firstControlIndex = i;
                break;
            }
        }

        // Fast path: no control characters present, return the original instance unchanged.
        if (firstControlIndex < 0)
        {
            return value;
        }

        StringBuilder builder = new(value.Length + 8);
        builder.Append(value, 0, firstControlIndex);

        for (int i = firstControlIndex; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
