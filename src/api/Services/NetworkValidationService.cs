using System.Net;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services;

/// <summary>
/// Service for validating network discovery settings including CIDR validation and overlap detection.
/// </summary>
public static class NetworkValidationService
{
    /// <summary>
    /// Validates network discovery settings including CIDR format, network address correctness, and overlap detection.
    /// </summary>
    /// <param name="settings">Network discovery settings to validate</param>
    /// <returns>Validation result with any errors found</returns>
    public static NetworkValidationResult ValidateSettings(NetworkDiscoverySettingsDto settings)
    {
        var result = new NetworkValidationResult();
        
        if (settings.NetworkRanges.Count == 0)
        {
            // No ranges is valid - discovery will be disabled
            return result;
        }

        // Validate each CIDR range
        var validNetworks = new List<(string cidr, IPAddress network, int prefix)>();
        
        foreach (var cidr in settings.NetworkRanges)
        {
            var trimmed = cidr.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var cidrValidation = ValidateCidr(trimmed);
            if (!cidrValidation.IsValid)
            {
                result.Errors.Add($"Invalid CIDR format: {trimmed} - {cidrValidation.Error}");
                if (!string.IsNullOrEmpty(cidrValidation.Suggestion))
                {
                    result.Suggestions.Add($"Consider using: {cidrValidation.Suggestion} instead of {trimmed}");
                }
            }
            else
            {
                validNetworks.Add((trimmed, cidrValidation.NetworkAddress!, cidrValidation.PrefixLength));
            }
        }

        // Check for overlapping networks
        var overlaps = FindOverlappingNetworks(validNetworks);
        foreach (var overlap in overlaps)
        {
            result.Warnings.Add($"Network ranges overlap: {overlap.cidr1} and {overlap.cidr2}");
        }

        // Additional validation
        if (settings.TimeoutMs < 100 || settings.TimeoutMs > 30000)
        {
            result.Errors.Add("Discovery timeout must be between 100ms and 30,000ms");
        }

        if (settings.MaxConcurrentScans < 1 || settings.MaxConcurrentScans > 100)
        {
            result.Errors.Add("Max concurrent scans must be between 1 and 100");
        }

        if (settings.Ports.Count == 0 && validNetworks.Count > 0)
        {
            result.Errors.Add("At least one port is required when network ranges are configured");
        }

        foreach (var port in settings.Ports)
        {
            if (port < 1 || port > 65535)
            {
                result.Errors.Add($"Invalid port number: {port} (must be 1-65535)");
            }
        }

        return result;
    }

    /// <summary>
    /// Validates a single CIDR notation string.
    /// </summary>
    /// <param name="cidr">CIDR string to validate (e.g., "192.168.1.0/24")</param>
    /// <returns>Validation result with network address and prefix if valid</returns>
    public static CidrValidationResult ValidateCidr(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return new CidrValidationResult { IsValid = false, Error = "CIDR cannot be empty" };

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return new CidrValidationResult { IsValid = false, Error = "CIDR must be in format IP/prefix (e.g., 192.168.1.0/24)" };

        // Parse IP address
        if (!IPAddress.TryParse(parts[0], out var ipAddress))
            return new CidrValidationResult { IsValid = false, Error = "Invalid IP address format" };

        if (ipAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return new CidrValidationResult { IsValid = false, Error = "Only IPv4 addresses are supported" };

        // Parse prefix length
        if (!int.TryParse(parts[1], out var prefixLength) || prefixLength < 0 || prefixLength > 32)
            return new CidrValidationResult { IsValid = false, Error = "Prefix length must be 0-32" };

        // Calculate the correct network address
        var networkAddress = GetNetworkAddress(ipAddress, prefixLength);
        
        // Check if the provided IP is actually the network address
        if (!ipAddress.Equals(networkAddress))
        {
            var suggestion = $"{networkAddress}/{prefixLength}";
            return new CidrValidationResult 
            { 
                IsValid = false, 
                Error = "IP address is not a network address (host bits should be zero)",
                Suggestion = suggestion,
                NetworkAddress = networkAddress,
                PrefixLength = prefixLength
            };
        }

        return new CidrValidationResult 
        { 
            IsValid = true, 
            NetworkAddress = networkAddress, 
            PrefixLength = prefixLength 
        };
    }

    /// <summary>
    /// Calculates the network address from an IP address and prefix length.
    /// </summary>
    private static IPAddress GetNetworkAddress(IPAddress ipAddress, int prefixLength)
    {
        var ipBytes = ipAddress.GetAddressBytes();
        var hostBits = 32 - prefixLength;

        // Calculate how many complete bytes to zero out
        var bytesToZero = hostBits / 8;
        var bitsToMask = hostBits % 8;

        // Zero out complete host bytes
        for (int i = ipBytes.Length - bytesToZero; i < ipBytes.Length; i++)
        {
            ipBytes[i] = 0;
        }

        // Mask partial byte if needed
        if (bitsToMask > 0)
        {
            var byteIndex = ipBytes.Length - bytesToZero - 1;
            var mask = (byte)(0xFF << bitsToMask);
            ipBytes[byteIndex] = (byte)(ipBytes[byteIndex] & mask);
        }

        return new IPAddress(ipBytes);
    }

    /// <summary>
    /// Finds overlapping networks in a collection.
    /// </summary>
    private static List<(string cidr1, string cidr2)> FindOverlappingNetworks(List<(string cidr, IPAddress network, int prefix)> networks)
    {
        var overlaps = new List<(string, string)>();

        for (int i = 0; i < networks.Count; i++)
        {
            for (int j = i + 1; j < networks.Count; j++)
            {
                if (NetworksOverlap(networks[i].network, networks[i].prefix, networks[j].network, networks[j].prefix))
                {
                    overlaps.Add((networks[i].cidr, networks[j].cidr));
                }
            }
        }

        return overlaps;
    }

    /// <summary>
    /// Checks if two networks overlap.
    /// </summary>
    private static bool NetworksOverlap(IPAddress network1, int prefix1, IPAddress network2, int prefix2)
    {
        // Check if network1 contains network2 or vice versa
        return IsNetworkInNetwork(network1, prefix1, network2, prefix2) ||
               IsNetworkInNetwork(network2, prefix2, network1, prefix1);
    }

    /// <summary>
    /// Checks if one network is contained within another.
    /// </summary>
    private static bool IsNetworkInNetwork(IPAddress testNetwork, int testPrefix, IPAddress containerNetwork, int containerPrefix)
    {
        // The container network must have a smaller or equal prefix (larger network)
        if (containerPrefix > testPrefix) return false;

        // Calculate network address of test network using container's prefix
        var testNetworkInContainer = GetNetworkAddress(testNetwork, containerPrefix);
        
        return testNetworkInContainer.Equals(containerNetwork);
    }
}

/// <summary>
/// Result of network discovery settings validation.
/// </summary>
public class NetworkValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Suggestions { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Result of individual CIDR validation.
/// </summary>
public class CidrValidationResult
{
    public bool IsValid { get; set; }
    public string Error { get; set; } = string.Empty;
    public string? Suggestion { get; set; }
    public IPAddress? NetworkAddress { get; set; }
    public int PrefixLength { get; set; }
}
