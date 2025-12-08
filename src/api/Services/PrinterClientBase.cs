using System.Diagnostics.CodeAnalysis;

namespace Farm.Web.Api.Services;

// Shared helpers for printer clients (Moonraker, PrusaLink)
public abstract class PrinterClientBase
{
    protected static bool IsLoopbackHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
               || host.StartsWith("127.", StringComparison.Ordinal);
    }

    // Normalize a base URL and ensure a default port if not present
    [SuppressMessage("Design", "CA1055:Uri return values should not be strings", Justification = "Non-breaking helper; Uri overload provided.")]
    protected static string NormalizeBaseUrl(string url, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        string trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            UriBuilder ub = new(trimmed);
            bool isHttp = string.Equals(ub.Scheme, "http", StringComparison.OrdinalIgnoreCase);
            int schemeDefaultPort = string.Equals(ub.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
            bool hadExplicitDefaultPort = trimmed.Contains($":{schemeDefaultPort}", StringComparison.Ordinal);

            if (isHttp && (ub.Port == -1 || (ub.Uri.IsDefaultPort && !hadExplicitDefaultPort)))
            {
                ub.Port = defaultPort;
            }
            return ub.Uri.ToString().TrimEnd('/');
        }
        catch
        {
            return url.TrimEnd('/');
        }
    }

    protected static string NormalizeBaseUrl(Uri url, int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(url);
        UriBuilder ub = new(url);
        if (string.Equals(ub.Scheme, "http", StringComparison.OrdinalIgnoreCase) && (ub.Port == -1 || ub.Uri.IsDefaultPort))
        {
            ub.Port = defaultPort;
        }
        return ub.Uri.ToString().TrimEnd('/');
    }

    // Normalize camera/thumbnail URLs that might be absolute with loopback host or relative
    [SuppressMessage("Design", "CA1055:Uri return values should not be strings", Justification = "Non-breaking helper; Uri-based overload pattern planned.")]
    protected static string NormalizeCameraUrl(string? url, string baseNorm)
    {
        ArgumentNullException.ThrowIfNull(baseNorm);
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        string s = url!.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out Uri? abs)
            && (string.Equals(abs.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(abs.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Uri baseUri = new(baseNorm);
                if (IsLoopbackHost(abs.Host))
                {
                    UriBuilder ub = new(abs)
                    {
                        Host = baseUri.Host,
                        Port = baseUri.IsDefaultPort ? -1 : baseUri.Port,
                        Scheme = baseUri.Scheme // align scheme with base
                    };
                    return ub.Uri.ToString();
                }
            }
            catch { }
            return abs.ToString();
        }

        // Relative or scheme-relative URL -> resolve against base using Uri composition
        if (s.StartsWith('/'))
        {
            return baseNorm.TrimEnd('/') + s;
        }
        try
        {
            Uri baseUri = new(baseNorm.EndsWith('/') ? baseNorm : baseNorm + "/");
            Uri combined = new(baseUri, s);
            return combined.ToString();
        }
        catch
        {
            // Fallback: conservative join without duplicating slashes
            string rel = s.StartsWith('/') ? s : "/" + s;
            return baseNorm.TrimEnd('/') + rel;
        }
    }
}
