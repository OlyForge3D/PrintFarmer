using System.Text.RegularExpressions;

namespace Farm.Infrastructure.Logging;

/// <summary>
/// Sanitizes user- or device-controlled strings before they are written to a log sink.
/// Prevents log forging (CodeQL cs/log-forging): an attacker who controls a logged value
/// (e.g. an HTTP header, a printer-supplied filename, a request parameter) could otherwise
/// embed CR/LF sequences to inject fake log lines or other control characters to corrupt
/// log output.
/// </summary>
public static partial class LogSanitizer
{
    /// <summary>
    /// Matches ASCII control characters other than CR/LF (which are handled separately via
    /// <see cref="string.Replace(string, string?)"/> so the escaping is expressed with the
    /// exact "remove/replace line breaks with String.Replace" idiom CodeQL's cs/log-forging
    /// barrier detection recognizes, rather than a manual character-by-character copy loop).
    /// </summary>
    [GeneratedRegex(@"[\x00-\x09\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex OtherControlCharsPattern();

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

        bool hasControlChar = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                hasControlChar = true;
                break;
            }
        }

        // Fast path: no control characters present, return the original instance unchanged.
        if (!hasControlChar)
        {
            return value;
        }

        // Escape CR/LF explicitly (in that order, so a "\r\n" pair becomes a single "\r\n"
        // literal rather than two independently-escaped characters) so they cannot start a
        // forged log line, then strip any other ASCII control characters.
        string escaped = value
            .Replace("\r\n", "\\r\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return OtherControlCharsPattern().Replace(escaped, string.Empty);
    }
}
