using System.Net;
using System.Runtime.InteropServices;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services;

/// <summary>
/// Handles URL rewriting for external services based on the runtime environment.
/// This allows the same configuration to work across Docker, native execution, and different platforms.
/// </summary>
public class NetworkUrlRewriteService(IUnifiedLoggingService logger, IConfiguration configuration)
{
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

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
            Uri uri = new(originalUrl);
            string rewrittenUrl = RewriteUri(uri);

            if (rewrittenUrl != originalUrl)
            {
                _logger.LogDebug($"URL rewritten for {serviceName ?? "unknown service"}: {originalUrl} -> {rewrittenUrl}", null, null);
            }

            return rewrittenUrl;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(ex, $"Invalid URL format, returning unchanged: {originalUrl}. Error: {ex.Message}", null, null);
            return originalUrl;
        }
    }

    private string RewriteUri(Uri uri)
    {
        RuntimeEnvironment environment = DetectEnvironment();

        // Check for explicit environment variable overrides first
        string? envOverride = _configuration[$"NetworkMapping:{uri.Host}:{uri.Port}"];
        if (!string.IsNullOrEmpty(envOverride))
        {
            _logger.LogDebug($"Using environment override for {uri.Host}:{uri.Port} -> {envOverride}", null, null);
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
                Uri hostDockerInternalUrl = ReplaceHostPort(uri, $"host.docker.internal:{uri.Port}");
                _logger.LogDebug($"Docker Desktop detected, rewriting to host.docker.internal: {hostDockerInternalUrl}", null, null);
                return hostDockerInternalUrl.ToString();
            }

            // Option 2: Use host network gateway (Linux Docker)
            string? gatewayOverride = _configuration["Docker:HostGateway"];
            if (!string.IsNullOrEmpty(gatewayOverride))
            {
                Uri gatewayUrl = ReplaceHostPort(uri, $"{gatewayOverride}:{uri.Port}");
                _logger.LogDebug($"Using Docker host gateway: {gatewayUrl}", null, null);
                return gatewayUrl.ToString();
            }
        }

        return uri.ToString();
    }

    private static string RewriteForWindowsNative(Uri uri)
    {
        // Native Windows execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private static string RewriteForMacOSNative(Uri uri)
    {
        // Native macOS execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private static string RewriteForLinuxNative(Uri uri)
    {
        // Native Linux execution - URLs should work as-is for local network
        return uri.ToString();
    }

    private static RuntimeEnvironment DetectEnvironment()
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

    private static bool IsDockerDesktop()
    {
        // Docker Desktop is typically used on Windows and macOS
        // Check for Docker Desktop specific environment variables or characteristics
        bool[] dockerDesktopIndicators = new[]
        {
            Environment.GetEnvironmentVariable("DOCKER_DESKTOP") == "true",
            // Docker Desktop typically uses these internal networks
            Environment.GetEnvironmentVariable("DOCKER_HOST")?.Contains("docker.io") == true,
            // Check if we're on Windows/macOS (where Docker Desktop is common)
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        };

        return dockerDesktopIndicators.Any(indicator => indicator);
    }

    private static bool IsLocalNetworkAddress(string host)
    {
        // Check if the host is a private IP address
        if (System.Net.IPAddress.TryParse(host, out IPAddress? ipAddress))
        {
            byte[] bytes = ipAddress.GetAddressBytes();

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
        string[] localHostnames = new[] { "localhost", "host.docker.internal" };
        return localHostnames.Contains(host.ToLowerInvariant());
    }

    private static Uri ReplaceHostPort(Uri originalUri, string newHostPort)
    {
        UriBuilder builder = new(originalUri);

        if (newHostPort.Contains(':'))
        {
            string[] parts = newHostPort.Split(':', 2);
            builder.Host = parts[0];
            if (int.TryParse(parts[1], out int port))
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
