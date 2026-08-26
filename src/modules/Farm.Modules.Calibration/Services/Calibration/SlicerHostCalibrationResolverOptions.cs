using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Services.Calibration;

/// <summary>
/// Strongly typed configuration for the split-deployment calibration profile resolver hop.
/// </summary>
/// <remarks>
/// The base URL is a fixed, operator-configured internal service address. It is never derived from
/// a request, never returned by a public capability document and never written to logs.
/// </remarks>
public sealed class SlicerHostCalibrationResolverOptions
{
    /// <summary>Configuration section bound to these options.</summary>
    public const string SectionName = "SlicerHost";

    /// <summary>Docker-internal default used by the generated compose templates.</summary>
#pragma warning disable S5332 // Container-internal service address on the private compose network; TLS terminates at the edge proxy.
    public const string ComposeDefaultBaseUrl = "http://slicer-host:5246";
#pragma warning restore S5332

    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 60;

    /// <summary>Base address of the slicer host that owns the calibration profile store.</summary>
    public Uri BaseUrl { get; init; } = null!;

    /// <summary>Bound on a single profile resolution round trip.</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Bound on the no-data availability probe.</summary>
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Hard cap on the response bytes buffered from the slicer host.</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Reads and validates the resolver configuration.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="options">The validated options when a base URL is configured.</param>
    /// <param name="error">A non-sensitive description of why a configured value was rejected.</param>
    /// <returns>
    /// <see langword="true"/> when a valid base URL is configured. <see langword="false"/> with a
    /// <see langword="null"/> <paramref name="error"/> means nothing was configured at all, which is
    /// a deployment that has not enabled the hop yet rather than a broken one.
    /// </returns>
    public static bool TryCreate(
        IConfiguration configuration,
        [NotNullWhen(true)] out SlicerHostCalibrationResolverOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = null;
        error = null;

        string? rawBaseUrl =
            configuration.GetValue<string>($"{SectionName}:BaseUrl") ??
            configuration.GetValue<string>("SLICER_HOST_URL");
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(rawBaseUrl.Trim(), UriKind.Absolute, out Uri? baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo))
        {
            error =
                $"'{SectionName}:BaseUrl' must be an absolute http(s) URL without query, fragment or user information.";
            return false;
        }

        if (!TryReadTimeout(configuration, "ResolveTimeoutSeconds", 10, out TimeSpan resolveTimeout, out error) ||
            !TryReadTimeout(configuration, "HealthTimeoutSeconds", 5, out TimeSpan healthTimeout, out error))
        {
            return false;
        }

        const int minResponseBytes = 1024;
        const int maxAllowedResponseBytes = 64 * 1024 * 1024;
        int maxResponseBytes = configuration.GetValue(
            $"{SectionName}:MaxResponseBytes",
            8 * 1024 * 1024);
        if (maxResponseBytes < minResponseBytes || maxResponseBytes > maxAllowedResponseBytes)
        {
            error =
                $"'{SectionName}:MaxResponseBytes' must be between {minResponseBytes} and {maxAllowedResponseBytes}.";
            return false;
        }

        options = new SlicerHostCalibrationResolverOptions
        {
            // A trailing slash keeps relative-route resolution anchored at the host root.
            BaseUrl = new Uri(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute),
            ResolveTimeout = resolveTimeout,
            HealthTimeout = healthTimeout,
            MaxResponseBytes = maxResponseBytes,
        };
        return true;
    }

    private static bool TryReadTimeout(
        IConfiguration configuration,
        string key,
        int defaultSeconds,
        out TimeSpan timeout,
        out string? error)
    {
        error = null;
        int seconds = configuration.GetValue($"{SectionName}:{key}", defaultSeconds);
        if (seconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            timeout = TimeSpan.Zero;
            error =
                $"'{SectionName}:{key}' must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.";
            return false;
        }

        timeout = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
