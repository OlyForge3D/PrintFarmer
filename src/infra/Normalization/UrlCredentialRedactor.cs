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
/// <item>Malformed userinfo can itself contain an unescaped "/", "?", or "#". A second round of
/// adversarial review (input such as <c>admin:pa@ss/word@printer.local</c>) showed that simply
/// bounding the "last @" search to the segment before the first such delimiter is not enough:
/// that segment can itself contain an earlier, spurious "@" (part of the password), which gets
/// mistaken for the real separator while the true last "@" sits just past the delimiter,
/// leaking part of the password. This helper therefore always searches for the last "@" across
/// the <b>entire remainder</b> of the string, then checks whether any "/", "?", or "#" appears
/// before that position. If one does, the userinfo boundary can no longer be trusted at all, so
/// the whole value is replaced with a placeholder instead of attempting a partial strip that
/// could itself mismatch the true boundary.</item>
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

        // Scan backward for the LAST '@' across the entire remainder of the string -- not just
        // the nominal authority segment (see remarks above for why bounding this search is
        // unsafe). Everything up to and including it is userinfo, per RFC 3986's "last
        // unescaped '@'" rule.
        int lastAt = serverUrl.LastIndexOf('@');

        if (lastAt < authorityStart)
        {
            // No credentials anywhere at/after the authority start; nothing to strip.
            return serverUrl;
        }

        if (lastAt >= authorityEnd)
        {
            // The true last '@' sits at or beyond the first path/query/fragment delimiter. This
            // means either the delimiter is itself inside malformed userinfo (so the real host
            // boundary can no longer be located reliably) or the '@' belongs to unrelated
            // content past the authority (e.g. an address in a query string) that this helper
            // cannot distinguish from the former. Either way, do not attempt a partial strip
            // that could itself land in the wrong place -- fail closed instead.
            return Placeholder;
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
