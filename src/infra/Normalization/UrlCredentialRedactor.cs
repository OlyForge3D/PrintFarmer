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
/// </remarks>
public static class UrlCredentialRedactor
{
    /// <summary>
    /// Returns <paramref name="serverUrl"/> with any userinfo (username/password) removed.
    /// Null, empty, and whitespace-only input pass through unchanged. A value with no
    /// <c>@</c> in its authority segment is returned unchanged (nothing to redact).
    /// </summary>
    public static string? Redact(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return serverUrl;
        }

        // Skip past "scheme://" when present; otherwise the whole string is authority-or-more.
        int authorityStart = 0;
        int schemeSeparator = serverUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            authorityStart = schemeSeparator + 3;
        }

        // The authority segment ends at the first path/query/fragment delimiter, or at the
        // end of the string when there is none.
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

        // Scan backward for the LAST '@' in the authority segment (see remarks above for why
        // this must not use Uri.UserInfo). Everything up to and including it is userinfo.
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
            // No credentials present in the authority segment; nothing to strip.
            return serverUrl;
        }

        string redacted = serverUrl[..authorityStart] + serverUrl[(lastAt + 1)..];

        // Degenerate input (e.g. an authority that is nothing but "@" characters) can strip
        // down to an empty or scheme-only remainder. Never emit that silently — make it
        // obvious in the log that credentials were removed rather than the field vanishing.
        return string.IsNullOrEmpty(redacted) ? "<redacted>" : redacted;
    }
}
