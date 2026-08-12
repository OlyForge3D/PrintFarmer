namespace Farm.Infrastructure;

/// <summary>
/// Produces credential-free HTTP URLs that authenticated clients may display or navigate to.
/// </summary>
public static class PrinterClientUrl
{
    /// <summary>
    /// Returns a normalized HTTP(S) URL without embedded credentials, query parameters, or fragments.
    /// </summary>
    public static string? Create(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.ToString().TrimEnd('/');
    }
}
