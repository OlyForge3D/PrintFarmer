namespace Farm.Infrastructure.Settings;

/// <summary>
/// Configuration for the optional, separately hosted PrintGuard failure-detection provider.
/// </summary>
public sealed class PrintGuardShadowSettings
{
    public const string SectionName = "PrintGuardShadow";

    public bool Enabled { get; set; }

    public string ApiUrl { get; set; } = string.Empty;

    public string? ApiToken { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 5;

    public int CaptureTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Validates the endpoint and timeouts without preventing other failure-detection providers from starting.
    /// </summary>
    public bool TryGetClassificationEndpoint(out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || RequestTimeoutSeconds is < 1 or > 60
            || CaptureTimeoutSeconds is < 1 or > 60)
        {
            return false;
        }

        endpoint = new Uri($"{baseUri.AbsoluteUri.TrimEnd('/')}/api/v1/classify", UriKind.Absolute);
        return true;
    }
}
