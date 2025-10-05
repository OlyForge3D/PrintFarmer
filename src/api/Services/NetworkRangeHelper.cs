using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;

namespace Farm.Web.Api.Services;

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
                var parts = range.Split('/');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefixLength))
                {
                    return ExpandCidrRange(network, prefixLength, logWarning);
                }
            }

            // Handle range notation (e.g., "192.168.1.1-192.168.1.254")
            if (range.Contains('-'))
            {
                var parts = range.Split('-');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0].Trim(), out var startIp) && IPAddress.TryParse(parts[1].Trim(), out var endIp))
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

        var networkBytes = network.GetAddressBytes();
        var hostBits = 32 - prefixLength;
        var maxHosts = Math.Min(1 << hostBits, 1024); // Limit to 1024 IPs max

        for (int i = 1; i < maxHosts - 1; i++) // Skip network and broadcast
        {
            var ipBytes = (byte[])networkBytes.Clone();
            ipBytes[3] += (byte)i;
            yield return new IPAddress(ipBytes).ToString();
        }
    }

    public static IEnumerable<string> ExpandIpRange(IPAddress startIp, IPAddress endIp, Action<string>? logWarning = null)
    {
        var start = BitConverter.ToUInt32(startIp.GetAddressBytes().Reverse().ToArray(), 0);
        var end = BitConverter.ToUInt32(endIp.GetAddressBytes().Reverse().ToArray(), 0);

        // Limit range size to prevent excessive scanning
        if (end - start > 1024)
        {
            logWarning?.Invoke($"IP range too large ({startIp}-{endIp}), limiting to 1024 addresses");
            end = start + 1024;
        }

        for (uint ip = start; ip <= end; ip++)
        {
            var bytes = BitConverter.GetBytes(ip).Reverse().ToArray();
            yield return new IPAddress(bytes).ToString();
        }
    }
}
