using System.Runtime.InteropServices;

namespace Farm.Web.Api.Services;

/// <summary>
/// Handles URL rewriting for external services based on the runtime environment.
/// This allows the same configuration to work across Docker, native execution, and different platforms.
/// </summary>
public class NetworkUrlRewriteService
{
    private readonly ILogger<NetworkUrlRewriteService> _logger;
    private readonly IConfiguration _configuration;

    public NetworkUrlRewriteService(ILogger<NetworkUrlRewriteService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Rewrites a URL to make it accessible from the current runtime environment.
    /// </summary>
    /// <param name="originalUrl">The original URL (e.g., "http://192.168.1.100:7912")</param>
    /// <param name="serviceName">Optional service name for logging</param>
    /// <returns>The rewritten URL that should be accessible from the current environment</returns>
    public string RewriteUrl(string originalUrl, string? serviceName = null)
    {
        if (string.IsNullOrEmpty(originalUrl))
        {
            return originalUrl;
        }

        try
        {
            var uri = new Uri(originalUrl);
            var rewrittenUrl = RewriteUri(uri);

            if (rewrittenUrl != originalUrl)
            {
                _logger.LogDebug("URL rewritten for {ServiceName}: {OriginalUrl} -> {RewrittenUrl}",
                    serviceName ?? "unknown service", originalUrl, rewrittenUrl);
            }

            return rewrittenUrl;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning("Invalid URL format, returning unchanged: {Url}. Error: {Error}",
                originalUrl, ex.Message);
            return originalUrl;
        }
    }

    private string RewriteUri(Uri uri)
    {
        var environment = DetectEnvironment();

        // Check for explicit environment variable overrides first
        var envOverride = _configuration[$"NetworkMapping:{uri.Host}:{uri.Port}"];
        if (!string.IsNullOrEmpty(envOverride))
        {
            _logger.LogDebug("Using environment override for {Host}:{Port} -> {Override}",
                uri.Host, uri.Port, envOverride);
            return ReplaceHostPort(uri, envOverride).ToString();
        }

        // Apply environment-specific rewrite rules
        return environment switch
        {
            RuntimeEnvironment.DockerContainer => RewriteForDockerContainer(uri),
            RuntimeEnvironment.WindowsNative => RewriteForWindowsNative(uri),
            RuntimeEnvironment.MacOSNative => RewriteForMacOSNative(uri),
            RuntimeEnvironment.LinuxNative => RewriteForLinuxNative(uri),
            _ => uri.ToString()
        };
    }

    private string RewriteForDockerContainer(Uri uri)
    {
        // In Docker containers, local network IPs need to be routed differently
        if (IsLocalNetworkAddress(uri.Host))
        {
            // Option 1: Try to use host.docker.internal (works on Windows/macOS Docker Desktop)
            if (IsDockerDesktop())
            {
                var hostDockerInternalUrl = ReplaceHostPort(uri, $"host.docker.internal:{uri.Port}");
                _logger.LogDebug("Docker Desktop detected, rewriting to host.docker.internal: {Url}",
                    hostDockerInternalUrl);
                return hostDockerInternalUrl.ToString();
            }

            // Option 2: Use host network gateway (Linux Docker)
            var gatewayOverride = _configuration["Docker:HostGateway"];
            if (!string.IsNullOrEmpty(gatewayOverride))
            {
                var gatewayUrl = ReplaceHostPort(uri, $"{gatewayOverride}:{uri.Port}");
                _logger.LogDebug("Using Docker host gateway: {Url}", gatewayUrl);
                return gatewayUrl.ToString();
            }
        }

        return uri.ToString();
    }

    private string RewriteForWindowsNative(Uri uri)
    {
        // Native Windows execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private string RewriteForMacOSNative(Uri uri)
    {
        // Native macOS execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private string RewriteForLinuxNative(Uri uri)
    {
        // Native Linux execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private RuntimeEnvironment DetectEnvironment()
    {
        // Check if running in Docker container
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return RuntimeEnvironment.DockerContainer;
        }

        // Detect OS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeEnvironment.WindowsNative;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeEnvironment.MacOSNative;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return RuntimeEnvironment.LinuxNative;
        }

        return RuntimeEnvironment.Unknown;
    }

    private bool IsDockerDesktop()
    {
        // Docker Desktop is typically used on Windows and macOS
        // Check for Docker Desktop specific environment variables or characteristics
        var dockerDesktopIndicators = new[]
        {
            Environment.GetEnvironmentVariable("DOCKER_DESKTOP") == "true",
            // Docker Desktop typically uses these internal networks
            Environment.GetEnvironmentVariable("DOCKER_HOST")?.Contains("docker.io") == true,
            // Check if we're on Windows/macOS (where Docker Desktop is common)
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        };

        return dockerDesktopIndicators.Any(indicator => indicator);
    }

    private bool IsLocalNetworkAddress(string host)
    {
        // Check if the host is a private IP address
        if (System.Net.IPAddress.TryParse(host, out var ipAddress))
        {
            var bytes = ipAddress.GetAddressBytes();

            // Check for private IP ranges (RFC 1918)
            return bytes.Length == 4 && (
                // 10.0.0.0/8
                bytes[0] == 10 ||
                // 172.16.0.0/12
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                // 192.168.0.0/16
                (bytes[0] == 192 && bytes[1] == 168) ||
                // 127.0.0.0/8 (localhost)
                bytes[0] == 127
            );
        }

        // Check for common local hostnames
        var localHostnames = new[] { "localhost", "host.docker.internal" };
        return localHostnames.Contains(host.ToLowerInvariant());
    }

    private Uri ReplaceHostPort(Uri originalUri, string newHostPort)
    {
        var builder = new UriBuilder(originalUri);

        if (newHostPort.Contains(':'))
        {
            var parts = newHostPort.Split(':', 2);
            builder.Host = parts[0];
            if (int.TryParse(parts[1], out var port))
            {
                builder.Port = port;
            }
        }
        else
        {
            builder.Host = newHostPort;
        }

        return builder.Uri;
    }

    private enum RuntimeEnvironment
    {
        Unknown,
        DockerContainer,
        WindowsNative,
        MacOSNative,
        LinuxNative
    }
}
