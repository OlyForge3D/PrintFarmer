using System.Net;
using System.Net.Sockets;
using Farm.Infrastructure.Settings;

namespace Farm.Infrastructure.Network;

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
    public static NetworkValidationResult ValidateSettings(NetworkDiscoverySettings settings)
    {
        NetworkValidationResult result = new();

        if (settings.DiscoverySubnets == null || settings.DiscoverySubnets.Count == 0)
        {
            // No ranges is valid - discovery will be disabled
            return result;
        }

        // Validate each CIDR range
        List<(string cidr, IPAddress network, int prefix)> validNetworks = new();

        foreach (string cidr in settings.DiscoverySubnets)
        {
            string trimmed = cidr.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            CidrValidationResult cidrValidation = ValidateCidr(trimmed);
            if (!cidrValidation.IsValid)
            {
                result._errors.Add($"Invalid CIDR format: {trimmed} - {cidrValidation.Error}");
                if (!string.IsNullOrEmpty(cidrValidation.Suggestion))
                {
                    result._suggestions.Add($"Consider using: {cidrValidation.Suggestion} instead of {trimmed}");
                }
            }
            else
            {
                validNetworks.Add((trimmed, cidrValidation.NetworkAddress!, cidrValidation.PrefixLength));
            }
        }

        // Check for overlapping networks
        List<(string cidr1, string cidr2)> overlaps = FindOverlappingNetworks(validNetworks);
        foreach ((string cidr1, string cidr2) in overlaps)
        {
            result._warnings.Add($"Network ranges overlap: {cidr1} and {cidr2}");
        }

        // Additional validation
        if (settings.ClientTimeoutMs < 100 || settings.ClientTimeoutMs > 30000)
        {
            result._errors.Add("Discovery timeout must be between 100ms and 30,000ms");
        }

        if (settings.MaxConcurrentRequests < 1 || settings.MaxConcurrentRequests > 100)
        {
            result._errors.Add("Max concurrent requests must be between 1 and 100");
        }

        // NOTE: Ports validation removed - each discovery probe handles its own backend-specific ports

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
        {
            return new CidrValidationResult { IsValid = false, Error = "CIDR cannot be empty" };
        }

        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            return new CidrValidationResult { IsValid = false, Error = "CIDR must be in format IP/prefix (e.g., 192.168.1.0/24)" };
        }

        // Parse IP address
        if (!IPAddress.TryParse(parts[0], out IPAddress? ipAddress))
        {
            return new CidrValidationResult { IsValid = false, Error = "Invalid IP address format" };
        }

        if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return new CidrValidationResult { IsValid = false, Error = "Only IPv4 addresses are supported" };
        }

        // Parse prefix length
        if (!int.TryParse(parts[1], out int prefixLength) || prefixLength < 0 || prefixLength > 32)
        {
            return new CidrValidationResult { IsValid = false, Error = "Prefix length must be 0-32" };
        }

        // Calculate the correct network address
        IPAddress networkAddress = GetNetworkAddress(ipAddress, prefixLength);

        // Check if the provided IP is actually the network address
        if (!ipAddress.Equals(networkAddress))
        {
            string suggestion = $"{networkAddress}/{prefixLength}";
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
        byte[] ipBytes = ipAddress.GetAddressBytes();
        int hostBits = 32 - prefixLength;

        // Calculate how many complete bytes to zero out
        int bytesToZero = hostBits / 8;
        int bitsToMask = hostBits % 8;

        // Zero out complete host bytes
        for (int i = ipBytes.Length - bytesToZero; i < ipBytes.Length; i++)
        {
            ipBytes[i] = 0;
        }

        // Mask partial byte if needed
        if (bitsToMask > 0)
        {
            int byteIndex = ipBytes.Length - bytesToZero - 1;
            byte mask = (byte)(0xFF << bitsToMask);
            ipBytes[byteIndex] = (byte)(ipBytes[byteIndex] & mask);
        }

        return new IPAddress(ipBytes);
    }

    /// <summary>
    /// Finds overlapping networks in a collection.
    /// </summary>
    private static List<(string cidr1, string cidr2)> FindOverlappingNetworks(List<(string cidr, IPAddress network, int prefix)> networks)
    {
        List<(string, string)> overlaps = new();

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
        if (containerPrefix > testPrefix)
        {
            return false;
        }

        // Calculate network address of test network using container's prefix
        IPAddress testNetworkInContainer = GetNetworkAddress(testNetwork, containerPrefix);

        return testNetworkInContainer.Equals(containerNetwork);
    }
}

/// <summary>
/// Result of network discovery settings validation.
/// </summary>
public class NetworkValidationResult
{
    internal readonly List<string> _errors = new();
    internal readonly List<string> _warnings = new();
    internal readonly List<string> _suggestions = new();
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public IReadOnlyList<string> Suggestions => _suggestions;
    public bool IsValid => _errors.Count == 0;
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
