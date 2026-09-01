namespace Farm.Infrastructure.Normalization;

/// <summary>
/// Strips embedded userinfo credentials (e.g. <c>user:pass@host</c>) from a server URL
/// before it is written to a log. This is a log-safety helper, not a general URL parser:
/// it must never throw, and on any doubt it must prefer discarding data over ever emitting
/// a password.
/// </summary>
/// <remarks>
/// A naive fix would parse with <see cref="Uri"/> and read <see cref="Uri.UserInfo"/> /
/// <see cref="Uri.Host"/>. That is exactly what let the credential leak through in the
/// first place: for a scheme-less authority such as <c>user:pass@host</c>,
/// <c>Uri.TryCreate(value, UriKind.Absolute, out _)</c> succeeds — "user" satisfies URI
/// scheme grammar, so .NET happily parses the whole thing as an <i>opaque</i> URI whose
/// <c>Scheme</c> is "user" and whose <c>Host</c>/<c>UserInfo</c> both come back empty, even
/// though a password is sitting right there in the opaque part. Routing the value through
/// <see cref="UrlNormalizer.EnsureBaseUri"/> first does not help either: that helper only
/// falls back to prepending <c>http://</c> when the *first* parse attempt fails outright,
/// and the scheme-shaped-username case is exactly the case where that first parse
/// succeeds (just with the wrong split).
///
/// This helper therefore never trusts <see cref="Uri"/>'s authority/userinfo split at all.
/// It locates the authority segment with plain string search (after <c>scheme://</c> if one
/// is present, otherwise from the start of the string, ending at the first <c>/</c>,
/// <c>?</c>, or <c>#</c>) and removes everything up to and including the <b>last</b>
/// <c>@</c> found in that segment. Per RFC 3986 userinfo is "everything up to the last
/// unescaped <c>@</c>" specifically so a password containing a literal <c>@</c> cannot be
/// mistaken for the userinfo/host separator; scanning for the last <c>@</c> ourselves
/// preserves that guarantee without depending on a successful <see cref="Uri"/> parse.
///
/// Two boundary cases need extra care, both found by adversarial review of the first version
/// of this helper:
/// <list type="bullet">
/// <item>A scheme separator ("://") must only be honored when it appears at the very start of
/// the string, immediately after RFC 3986 scheme grammar (letters, then letters/digits/"+"/
/// "-"/"."). Searching for "://" anywhere in the value is unsafe: a scheme-less, credentialed
/// authority followed later by a path/query that itself contains "://" (for example a
/// callback URL embedded in the query string) would otherwise move the authority boundary
/// past the real credentials and let them leak through untouched.</item>
/// <item>Malformed userinfo can itself contain an unescaped "/", "?", or "#", which would stop
/// the authority scan before it ever reaches the real "@". Rather than trust that boundary and
/// conclude "no credentials found", this helper checks whether an "@" exists anywhere later in
/// the string; if so, the input is too ambiguous to safely strip, so the whole value is
/// replaced with a placeholder instead of ever being echoed back.</item>
/// </list>
/// </remarks>
public static class UrlCredentialRedactor
{
    private const string Placeholder = "<redacted>";

    /// <summary>
    /// Returns <paramref name="serverUrl"/> with any userinfo (username/password) removed.
    /// Null, empty, and whitespace-only input pass through unchanged. A value with no
    /// <c>@</c> anywhere at or after the authority start is returned unchanged (nothing to
    /// redact). Ambiguous or degenerate input that cannot be safely stripped down to a
    /// credential-free remainder is replaced with a fixed placeholder rather than ever risking
    /// an unredacted (or partially redacted) password reaching the log.
    /// </summary>
    public static string? Redact(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return serverUrl;
        }

        // Skip past "scheme://" when present at the very start of the string; otherwise the
        // whole string is authority-or-more. See remarks above for why this must be anchored
        // to the start rather than found anywhere in the value.
        int authorityStart = ScanLeadingScheme(serverUrl);

        // The authority segment nominally ends at the first path/query/fragment delimiter, or
        // at the end of the string when there is none.
        int authorityEnd = serverUrl.Length;
        for (int i = authorityStart; i < serverUrl.Length; i++)
        {
            char c = serverUrl[i];
            if (c is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }

        // Scan backward for the LAST '@' in the nominal authority segment (see remarks above
        // for why this must not use Uri.UserInfo). Everything up to and including it is
        // userinfo.
        int lastAt = -1;
        for (int i = authorityEnd - 1; i >= authorityStart; i--)
        {
            if (serverUrl[i] == '@')
            {
                lastAt = i;
                break;
            }
        }

        if (lastAt < 0)
        {
            // Nothing found within the nominal authority segment. Before concluding there are
            // no credentials, check whether an '@' exists anywhere further into the string: if
            // so, an unescaped '/', '?', or '#' inside malformed userinfo cut the scan short,
            // and we cannot safely tell where the real host begins. Fail closed.
            return serverUrl.IndexOf('@', authorityStart) >= 0 ? Placeholder : serverUrl;
        }

        // Degenerate input (e.g. an authority that is nothing but "@" characters) can strip
        // down to an empty host. Never emit that silently — make it obvious in the log that
        // credentials were removed rather than the field vanishing or the boundary collapsing
        // to nothing meaningful.
        if (lastAt + 1 >= authorityEnd)
        {
            return Placeholder;
        }

        return serverUrl[..authorityStart] + serverUrl[(lastAt + 1)..];
    }

    /// <summary>
    /// Returns the index immediately after a leading "scheme://" prefix per RFC 3986 scheme
    /// grammar (<c>ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )</c>), or 0 when no such prefix is
    /// present at the very start of <paramref name="value"/>.
    /// </summary>
    private static int ScanLeadingScheme(string value)
    {
        if (value.Length == 0 || !char.IsAsciiLetter(value[0]))
        {
            return 0;
        }

        int i = 1;
        while (i < value.Length && (char.IsAsciiLetterOrDigit(value[i]) || value[i] is '+' or '-' or '.'))
        {
            i++;
        }

        if (i + 2 < value.Length && value[i] == ':' && value[i + 1] == '/' && value[i + 2] == '/')
        {
            return i + 3;
        }

        return 0;
    }
}
