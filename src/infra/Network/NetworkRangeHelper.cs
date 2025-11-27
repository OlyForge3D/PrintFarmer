using System.Net;

namespace Farm.Infrastructure.Network;

/// <summary>
/// Helper for expanding network ranges (CIDR, IP range, single IP) into individual IP addresses.
/// </summary>
public static class NetworkRangeHelper
{
    public static IEnumerable<string> ExpandNetworkRange(string range, Action<string>? logWarning = null)
    {
        try
        {
            // Handle CIDR notation (e.g., "192.168.1.0/24")
            if (range.Contains('/'))
            {
                string[] parts = range.Split('/');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out IPAddress? network) && int.TryParse(parts[1], out int prefixLength))
                {
                    return ExpandCidrRange(network, prefixLength, logWarning);
                }
            }

            // Handle range notation (e.g., "192.168.1.1-192.168.1.254")
            if (range.Contains('-'))
            {
                string[] parts = range.Split('-');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out IPAddress? startIp) && IPAddress.TryParse(parts[1].Trim(), out IPAddress? endIp))
                {
                    return ExpandIpRange(startIp, endIp, logWarning);
                }
            }

            // Single IP address
            if (IPAddress.TryParse(range, out _))
            {
                return new[] { range };
            }
        }
        catch (Exception ex)
        {
            logWarning?.Invoke($"Failed to expand network range '{range}': {ex.Message}");
        }

        return Array.Empty<string>();
    }

    public static IEnumerable<string> ExpandCidrRange(IPAddress network, int prefixLength, Action<string>? logWarning = null)
    {
        // Limit to /16 or smaller subnets to avoid excessive scanning
        if (prefixLength < 16)
        {
            logWarning?.Invoke($"CIDR range too large (/{prefixLength}), limiting to /16");
            prefixLength = 16;
        }

        byte[] networkBytes = network.GetAddressBytes();
        int hostBits = 32 - prefixLength;
        int maxHosts = Math.Min(1 << hostBits, 1024); // Limit to 1024 IPs max

        for (int i = 1; i < maxHosts - 1; i++) // Skip network and broadcast
        {
            byte[] ipBytes = (byte[])networkBytes.Clone();
            ipBytes[3] += (byte)i;
            yield return new IPAddress(ipBytes).ToString();
        }
    }

    public static IEnumerable<string> ExpandIpRange(IPAddress startIp, IPAddress endIp, Action<string>? logWarning = null)
    {
        uint start = BitConverter.ToUInt32(startIp.GetAddressBytes().Reverse().ToArray(), 0);
        uint end = BitConverter.ToUInt32(endIp.GetAddressBytes().Reverse().ToArray(), 0);

        // Limit range size to prevent excessive scanning
        if (end - start > 1024)
        {
            logWarning?.Invoke($"IP range too large ({startIp}-{endIp}), limiting to 1024 addresses");
            end = start + 1024;
        }

        for (uint ip = start; ip <= end; ip++)
        {
            byte[] bytes = BitConverter.GetBytes(ip).Reverse().ToArray();
            yield return new IPAddress(bytes).ToString();
        }
    }
}
