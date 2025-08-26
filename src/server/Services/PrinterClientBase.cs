namespace Farm.Web.Server.Services;

// Shared helpers for printer clients (Moonraker, PrusaLink)
public abstract class PrinterClientBase
{
    protected static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
           || host.StartsWith("127.", StringComparison.Ordinal);

    // Normalize a base URL and ensure a default port if not present
    protected static string NormalizeBaseUrl(string url, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var trimmed = url.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        try
        {
            var ub = new UriBuilder(trimmed);
            if (ub.Port == -1)
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

    // Normalize camera/thumbnail URLs that might be absolute with loopback host or relative
    protected static string NormalizeCameraUrl(string? url, string baseNorm)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var s = url!.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var abs))
        {
            try
            {
                var baseUri = new Uri(baseNorm);
                if (IsLoopbackHost(abs.Host))
                {
                    var ub = new UriBuilder(abs)
                    {
                        Host = baseUri.Host,
                        Scheme = baseUri.Scheme // align scheme with base
                    };
                    return ub.Uri.ToString();
                }
            }
            catch { }
            return abs.ToString();
        }
        // Relative path -> anchor to base
        var rel = s.StartsWith('/') ? s : "/" + s;
        return baseNorm + rel;
    }
}
