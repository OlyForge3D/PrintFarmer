using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services;

public class NetworkDiscoverySettingsService : INetworkDiscoverySettingsService
{
    private readonly ILogger<NetworkDiscoverySettingsService> _logger;
    private NetworkDiscoverySettingsDto? _settings;
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "network.discovery.settings.json");

    public NetworkDiscoverySettingsService(ILogger<NetworkDiscoverySettingsService> logger)
    {
        _logger = logger;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<NetworkDiscoverySettingsDto>(json);
                if (cfg is not null) _settings = cfg;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load network discovery settings from {Path}", _path);
        }
    }

    public NetworkDiscoverySettingsDto GetSettings()
    {
        if (_settings != null && _settings.NetworkRanges.Count > 0)
        {
            return _settings;
        }

        // If no settings exist or no ranges configured, return settings with dynamic ranges
        var dynamicRanges = GetDynamicNetworkRanges();
        return new NetworkDiscoverySettingsDto(
            dynamicRanges.Count > 0 ? dynamicRanges : GetFallbackNetworkRanges(),
            _settings?.TimeoutMs ?? 100,
            _settings?.MaxConcurrentScans ?? 20,
            _settings?.Ports ?? new List<int> { 80, 7125 }
        );
    }

    public void SaveSettings(NetworkDiscoverySettingsDto settings)
    {
        _settings = settings;
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
            _logger.LogInformation("Saved network discovery settings to {Path}", _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save network discovery settings to {Path}", _path);
        }
    }

    public List<string> GetDynamicNetworkRanges()
    {
        var networks = new List<string>();
        
        try
        {
            // Use a simpler approach to detect current network
            var hostName = Dns.GetHostName();
            var hostEntry = Dns.GetHostEntry(hostName);
            
            foreach (var address in hostEntry.AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork && 
                    !IPAddress.IsLoopback(address) &&
                    IsPrivateNetwork(address))
                {
                    // Assume /24 subnet for private networks
                    var networkAddr = GetNetworkAddressForPrivateIP(address);
                    if (networkAddr != null && !networks.Contains(networkAddr))
                    {
                        networks.Add(networkAddr);
                        _logger.LogDebug("Detected network from host IP: {IP} -> {Network}", address, networkAddr);
                    }
                }
            }

            _logger.LogInformation("Detected {Count} network ranges from host interfaces: {Networks}", 
                networks.Count, string.Join(", ", networks));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect network ranges from host interfaces");
            return GetFallbackNetworkRanges();
        }

        return networks.Count > 0 ? networks : GetFallbackNetworkRanges();
    }

    private static bool IsPrivateNetwork(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        
        // 10.0.0.0/8
        if (bytes[0] == 10) return true;
        
        // 172.16.0.0/12
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
        
        // 192.168.0.0/16
        if (bytes[0] == 192 && bytes[1] == 168) return true;
        
        return false;
    }

    private static string? GetNetworkAddressForPrivateIP(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        
        // For private networks, assume common subnet masks
        if (bytes[0] == 10)
        {
            // 10.x.x.x -> assume /24 (10.x.x.0/24)
            return $"10.{bytes[1]}.{bytes[2]}.0/24";
        }
        else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
        {
            // 172.16-31.x.x -> assume /24 (172.x.x.0/24) 
            return $"172.{bytes[1]}.{bytes[2]}.0/24";
        }
        else if (bytes[0] == 192 && bytes[1] == 168)
        {
            // 192.168.x.x -> assume /24 (192.168.x.0/24)
            return $"192.168.{bytes[2]}.0/24";
        }
        
        return null;
    }

    private static string? GetNetworkAddress(IPAddress ip, IPAddress mask)
    {
        try
        {
            var ipBytes = ip.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();
            var networkBytes = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            }

            var network = new IPAddress(networkBytes);
            var cidr = GetCidrFromMask(mask);
            return $"{network}/{cidr}";
        }
        catch
        {
            return null;
        }
    }

    private static int GetCidrFromMask(IPAddress mask)
    {
        var maskBytes = mask.GetAddressBytes();
        var cidr = 0;
        
        foreach (var b in maskBytes)
        {
            cidr += Convert.ToString(b, 2).Count(c => c == '1');
        }
        
        return cidr;
    }

    private static List<string> GetFallbackNetworkRanges()
    {
        return new List<string>
        {
            "10.0.0.0/24",
            "192.168.1.0/24",
            "192.168.0.0/24", 
            "192.168.2.0/24"
        };
    }
}
