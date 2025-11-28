using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;

namespace Farm.Tools.AdminCli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
#pragma warning disable CA1303 // Suppress localization warnings for CLI output
        Dictionary<string, string> argsDic = ParseArgs(args);
        if (argsDic.ContainsKey("help") || argsDic.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        // Load saved discovery config and merge with CLI args (CLI args take precedence)
        if (argsDic.ContainsKey("discover") && !argsDic.ContainsKey("range"))
        {
            DiscoveryConfig? savedConfig = await LoadDiscoveryConfigAsync();
            if (savedConfig != null)
            {
                if (!string.IsNullOrEmpty(savedConfig.Range))
                {
                    argsDic["range"] = savedConfig.Range;
                }
                if (!string.IsNullOrEmpty(savedConfig.Interface))
                {
                    argsDic["interface"] = savedConfig.Interface;
                }
                if (!argsDic.ContainsKey("timeout"))
                {
                    argsDic["timeout"] = savedConfig.Timeout.ToString();
                }
                if (!argsDic.ContainsKey("concurrent"))
                {
                    argsDic["concurrent"] = savedConfig.Concurrent.ToString();
                }
                Console.WriteLine("[AdminCli] Loaded saved discovery config from last run");
            }
        }

        string baseUrl = argsDic.GetValueOrDefault("base-url", "http://localhost:5245").TrimEnd('/');
        using HttpClient http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        if (argsDic.ContainsKey("status"))
        {
            await PrintStatusAsync(http);
            return 0;
        }

        // Handle sample CSV generation
        if (argsDic.ContainsKey("sample-csv"))
        {
            return await HandleSampleCsvAsync(argsDic);
        }

        // Handle discovery commands
        if (argsDic.ContainsKey("discover"))
        {
            return await HandleDiscoveryAsync(argsDic);
        }

        bool hasUsername = argsDic.TryGetValue("username", out string? username);
        bool hasEmail = argsDic.TryGetValue("email", out string? email);
        bool hasPassword = argsDic.TryGetValue("password", out string? password);
        if (!hasUsername || !hasEmail || !hasPassword)
        {
            await Console.Error.WriteLineAsync("ERROR: --username, --email and --password are required unless using --status, --discover, or --sample-csv.");
            PrintHelp();
            return 1;
        }

        if (password!.Length < 12)
        {
            await Console.Error.WriteLineAsync("ERROR: Password must be at least 12 characters (server requirement).");
            return 2;
        }

        Console.WriteLine($"[AdminCli] Attempting initial admin creation against {baseUrl} ...");

        SetupStatus? status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
        if (status == null)
        {
            await Console.Error.WriteLineAsync("ERROR: Could not retrieve setup status.");
            return 3;
        }

        if (!status.NeedsSetup)
        {
            Console.WriteLine("Admin already exists; attempting idempotent login with supplied credentials...");
        }

        var payload = new
        {
            username,
            email,
            password,
            firstName = argsDic.GetValueOrDefault("first-name", "Admin"),
            lastName = argsDic.GetValueOrDefault("last-name", "User")
        };

        HttpResponseMessage response = await http.PostAsJsonAsync("/api/setup/initial-admin", payload, Json);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync($"Server returned {(int)response.StatusCode} {response.StatusCode}\n{body}");
            return 4;
        }

        try
        {
            AuthResult? result = JsonSerializer.Deserialize<AuthResult>(body, Json);
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Token))
            {
                await Console.Error.WriteLineAsync("Unexpected response: " + body);
                return 5;
            }

            Console.WriteLine("SUCCESS: Admin ready.");
            Console.WriteLine("Username: " + payload.username);
            Console.WriteLine("Email:    " + payload.email);
            Console.WriteLine("JWT:      " + result.Token);
            Console.WriteLine("Expires:  " + result.ExpiresAt);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Failed to parse response: " + ex.Message + "\nRaw: " + body);
            return 6;
        }
#pragma warning restore CA1303
    }

    private static async Task<int> HandleSampleCsvAsync(Dictionary<string, string> argsDic)
    {
#pragma warning disable CA1303
        try
        {
            string outputFile = argsDic.GetValueOrDefault("output", "");
            string csv = GenerateSampleCsv();

            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                await File.WriteAllTextAsync(outputFile, csv);
                Console.WriteLine($"✓ Sample CSV generated: {outputFile}");
            }
            else
            {
                Console.WriteLine(csv);
            }

            Console.WriteLine("\nSample CSV template with examples for each backend type.");
            Console.WriteLine("Edit the file to customize for your printers, then import via API:");
            Console.WriteLine("  POST /api/printers/import (with CSV file attachment)");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error generating sample CSV: {ex.Message}");
            return 1;
        }
#pragma warning restore CA1303
    }

    private static string GenerateSampleCsv()
    {
        StringBuilder csv = new StringBuilder();
        _ = csv.AppendLine("Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,IsEnabled");

        // Example rows for each backend type
        _ = csv.AppendLine("\"Moonraker Printer\",\"192.168.1.100\",\"Moonraker\",\"7125\",\"80\",\"Creality\",\"Ender-3 Max\",\"Main production printer\",\"false\"");
        _ = csv.AppendLine("\"PrusaLink Printer\",\"192.168.1.101\",\"PrusaLink\",\"80\",\"443\",\"Prusa\",\"MK3S+\",\"High precision prints\",\"false\"");
        _ = csv.AppendLine("\"SDCP Printer\",\"192.168.1.102\",\"SDCP\",\"80\",\"80\",\"Bambu Lab\",\"X1 Carbon\",\"Fast prints\",\"false\"");
        _ = csv.AppendLine("\"OctoPrint Printer\",\"192.168.1.103\",\"Moonraker\",\"7125\",\"80\",\"Anet\",\"A8 Plus\",\"Legacy setup with OctoPrint\",\"false\"");

        return csv.ToString();
    }

    private static async Task<int> HandleDiscoveryAsync(Dictionary<string, string> argsDic)
    {
#pragma warning disable CA1303
        try
        {
            string outputFormat = argsDic.GetValueOrDefault("format", "json").ToLowerInvariant();
            string outputFile = argsDic.GetValueOrDefault("output", "");
            bool noApproval = argsDic.ContainsKey("no-approval");
            List<string> rangeConstraints = argsDic.GetValueOrDefault("range", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).ToList();
            List<string> interfaceConstraints = argsDic.GetValueOrDefault("interface", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).ToList();
            int probeTimeoutMs = int.TryParse(argsDic.GetValueOrDefault("timeout", "200"), out int t) ? t : 200;
            int maxConcurrentScans = int.TryParse(argsDic.GetValueOrDefault("concurrent", "10"), out int c) ? c : 10;

            Console.WriteLine($"[AdminCli] Starting local network discovery...");
            Console.WriteLine($"  Format: {outputFormat}");
            Console.WriteLine($"  Approval required: {!noApproval}");
            Console.WriteLine($"  Probe timeout: {probeTimeoutMs}ms");
            Console.WriteLine($"  Concurrent scans: {maxConcurrentScans}");
            if (rangeConstraints.Count > 0)
            {
                Console.WriteLine($"  IP Range Filter: {string.Join(", ", rangeConstraints)}");
            }
            if (interfaceConstraints.Count > 0)
            {
                Console.WriteLine($"  Interface Filter: {string.Join(", ", interfaceConstraints)}");
            }

            // Save discovery config for next run
            await SaveDiscoveryConfigAsync(new DiscoveryConfig
            {
                Range = string.Join(",", rangeConstraints),
                Interface = string.Join(",", interfaceConstraints),
                Timeout = probeTimeoutMs,
                Concurrent = maxConcurrentScans,
                Format = outputFormat,
                NoApproval = noApproval
            });

            // Create discovery probes
            INetworkDiscoveryProbe[] probes = new INetworkDiscoveryProbe[]
            {
                new MoonrakerDiscoveryProbe(),
                new PrusaLinkDiscoveryProbe(),
                new OctoPrintDiscoveryProbe(),
                new SdcpDiscoveryProbe()
            };

            // Perform local discovery without API dependency
            List<DiscoveredPrinterInfo> discovered = await PerformLocalDiscoveryAsync(probes, rangeConstraints, interfaceConstraints, probeTimeoutMs, maxConcurrentScans);

            Console.WriteLine($"\n[AdminCli] Discovery complete. Found {discovered.Count} printer(s).\n");

            if (discovered.Count == 0)
            {
                Console.WriteLine("No printers found during discovery.");
                return 0;
            }

            // Format output
            string formattedOutput = outputFormat switch
            {
                "csv" => FormatAsCSV(discovered.ToArray(), !noApproval),
                "json" => FormatAsJSON(discovered.ToArray(), !noApproval),
                _ => throw new ArgumentException($"Unsupported format: {outputFormat}")
            };

            // Output results
            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                await File.WriteAllTextAsync(outputFile, formattedOutput);
                Console.WriteLine($"Results saved to: {outputFile}");
            }
            else
            {
                Console.WriteLine(formattedOutput);
            }

            Console.WriteLine($"\nNext steps:");
            Console.WriteLine($"1. Review the discovered printers above");
            Console.WriteLine($"2. Edit manufacturer, model, and isEnabled (set to true when approved)");
            Console.WriteLine($"3. Import via API: POST /api/printers/import");

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error during discovery: {ex.Message}");
            return 1;
        }
#pragma warning restore CA1303
    }

    /// <summary>
    /// Performs network discovery locally without requiring API service.
    /// Scans local network using shared discovery probes with progress reporting.
    /// </summary>
    /// <param name="probes">Discovery probes for each backend type</param>
    /// <param name="rangeConstraints">Optional list of CIDR ranges (e.g., 192.168.1.0/24) to constrain discovery</param>
    /// <param name="interfaceConstraints">Optional list of interface names (e.g., en0) to constrain discovery</param>
    private static async Task<List<DiscoveredPrinterInfo>> PerformLocalDiscoveryAsync(
        INetworkDiscoveryProbe[] probes,
        List<string>? rangeConstraints = null,
        List<string>? interfaceConstraints = null,
        int probeTimeoutMs = 200,
        int maxConcurrentScans = 10)
    {
        rangeConstraints ??= new List<string>();
        interfaceConstraints ??= new List<string>();

        List<DiscoveredPrinterInfo> discovered = new List<DiscoveredPrinterInfo>();

        // Get local network interfaces
        List<NetworkInterface> interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                        i.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .ToList();

        // Apply interface filter if specified
        if (interfaceConstraints.Count > 0)
        {
            interfaces = interfaces.Where(i => interfaceConstraints.Contains(i.Name)).ToList();
            if (interfaces.Count == 0)
            {
                Console.WriteLine($"[Discovery] No matching interfaces found. Available: {string.Join(", ", NetworkInterface.GetAllNetworkInterfaces().Select(i => i.Name))}");
                return discovered;
            }
        }

        Console.WriteLine($"\n[Discovery] Found {interfaces.Count} active network interface(s)\n");

        foreach (NetworkInterface iface in interfaces)
        {
            IPInterfaceProperties ipProps = iface.GetIPProperties();
            foreach (UnicastIPAddressInformation? unicast in ipProps.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                IPAddress ip = unicast.Address;
                IPAddress subnet = unicast.IPv4Mask;

                // Calculate network range from IP and subnet mask
                IPAddress network = GetNetworkAddress(ip, subnet);
                IPAddress broadcast = GetBroadcastAddress(ip, subnet);
                string cidr = $"{network}/{GetCIDR(subnet)}";

                // Check if this range matches any constraints
                if (rangeConstraints.Count > 0)
                {
                    bool matchesConstraint = rangeConstraints.Any(constraint =>
                    {
                        try
                        {
                            return IpRangeContainsRange(constraint, network, broadcast);
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!matchesConstraint)
                    {
                        Console.WriteLine($"[Discovery] Skipping {cidr} (does not match constraints)");
                        continue;
                    }
                }

                Console.WriteLine($"[Discovery] Scanning network: {cidr}");
                Console.WriteLine($"  Interface: {iface.Name} ({ip})");
                Console.WriteLine($"  Range: {network} → {broadcast}");

                // If range constraints exist, use the intersection of interface range and constraint range
                IPAddress scanStart = network;
                IPAddress scanEnd = broadcast;

                if (rangeConstraints.Count > 0)
                {
                    // Find the tightest (smallest) constraint that applies to this interface
                    string? applicableConstraint = rangeConstraints.FirstOrDefault(constraint =>
                    {
                        try
                        {
                            return IpRangeContainsRange(constraint, network, broadcast);
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (applicableConstraint != null)
                    {
                        string[] parts = applicableConstraint.Split('/');
                        if (parts.Length == 2 && int.TryParse(parts[1], out int constraintCidr))
                        {
                            IPAddress constraintIp = IPAddress.Parse(parts[0]);
                            IPAddress constraintSubnet = CIDRToSubnetMask(constraintCidr);
                            scanStart = GetNetworkAddress(constraintIp, constraintSubnet);
                            scanEnd = GetBroadcastAddress(constraintIp, constraintSubnet);
                            Console.WriteLine($"  Constraint applied: {scanStart}/{constraintCidr} → {scanEnd}");
                        }
                    }
                }

                // Scan IP range with concurrency
                uint start = BitConverter.ToUInt32(scanStart.GetAddressBytes().Reverse().ToArray(), 0);
                uint end = BitConverter.ToUInt32(scanEnd.GetAddressBytes().Reverse().ToArray(), 0);
                uint total = end - start;
                int scanCount = 0;

                using SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrentScans, maxConcurrentScans);
                List<Task> scanTasks = new List<Task>();

                for (uint i = start + 1; i < end; i++)
                {
                    uint ipValue = i; // Capture for closure
                    Task task = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            int scanCountLocal = Interlocked.Increment(ref scanCount);

                            // Show progress every 10 IPs scanned
                            if (scanCountLocal % 10 == 0 || scanCountLocal == 1)
                            {
                                int progress = (int)((scanCountLocal / (double)total) * 100);
                                Console.Write($"\r  Scanning... [{progress}%] ({scanCountLocal}/{total} IPs checked, {discovered.Count} found)");
                            }

                            byte[] ipBytes = BitConverter.GetBytes(ipValue).Reverse().ToArray();
                            string targetIp = new IPAddress(ipBytes).ToString();

                            // Probe with each discovery probe and collect all results
                            List<ProbeResult> probeResults = new();
                            foreach (INetworkDiscoveryProbe probe in probes)
                            {
                                try
                                {
                                    ProbeResult? result = await probe.ProbeAsync(targetIp, probeTimeoutMs, CancellationToken.None);
                                    if (result != null)
                                    {
                                        probeResults.Add(result);
                                    }
                                }
                                catch
                                {
                                    // Probe failed, try next one
                                }
                            }

                            // Use the result with highest confidence score
                            if (probeResults.Count > 0)
                            {
                                ProbeResult bestResult = probeResults.MaxBy(r => r.ConfidenceScore)!;
                                DiscoveredPrinterDto result = bestResult.Printer;

                                DiscoveredPrinterInfo printerInfo = new DiscoveredPrinterInfo
                                {
                                    IpAddress = result.IpAddress,
                                    Backend = result.Backend.ToString(),
                                    BackendPort = result.BackendPort,
                                    FrontendPort = result.FrontendPort,
                                    FriendlyName = result.Name
                                };

                                // Check if already discovered (lock for thread safety)
                                lock (discovered)
                                {
                                    if (!discovered.Any(d => d.IpAddress == result.IpAddress && d.Backend == result.Backend.ToString()))
                                    {
                                        discovered.Add(printerInfo);
                                        Console.Write("\r");
                                        Console.WriteLine($"  ✓ Found {result.Backend.ToString(),12} at {result.IpAddress,15} ({bestResult.Reason}) ({discovered.Count} total)");
                                    }
                                }
                            }
                        }
                        finally
                        {
                            _ = semaphore.Release();
                        }
                    });
                    scanTasks.Add(task);
                }

                // Wait for all scans to complete
                await Task.WhenAll(scanTasks);
                Console.WriteLine($"\r  Scan complete. Checked {scanCount} IPs.\n");
            }
        }

        // Sort results by IP address for deterministic output
        // This ensures consistent ordering across multiple discovery runs
        discovered.Sort((a, b) => CompareIpAddresses(a.IpAddress, b.IpAddress));
        return discovered;
    }

    /// <summary>
    /// Checks if a constraint range (CIDR) contains the target range.
    /// </summary>
    private static bool IpRangeContainsRange(string cidrConstraint, IPAddress targetNetwork, IPAddress targetBroadcast)
    {
        string[] parts = cidrConstraint.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int constraintCidr))
        {
            throw new ArgumentException($"Invalid CIDR format: {cidrConstraint}");
        }

        IPAddress constraintIp = IPAddress.Parse(parts[0]);
        IPAddress constraintSubnet = CIDRToSubnetMask(constraintCidr);
        IPAddress constraintNetwork = GetNetworkAddress(constraintIp, constraintSubnet);
        IPAddress constraintBroadcast = GetBroadcastAddress(constraintIp, constraintSubnet);

        uint targetStart = BitConverter.ToUInt32(targetNetwork.GetAddressBytes().Reverse().ToArray(), 0);
        uint targetEnd = BitConverter.ToUInt32(targetBroadcast.GetAddressBytes().Reverse().ToArray(), 0);
        uint constraintStart = BitConverter.ToUInt32(constraintNetwork.GetAddressBytes().Reverse().ToArray(), 0);
        uint constraintEnd = BitConverter.ToUInt32(constraintBroadcast.GetAddressBytes().Reverse().ToArray(), 0);

        // Check if constraint overlaps with or is contained in target range
        // Returns true if: constraint is fully within target, or they overlap, or target is fully within constraint
        return !(constraintEnd < targetStart || constraintStart > targetEnd);
    }

    /// <summary>
    /// Compares two IP addresses numerically for sorting.
    /// </summary>
    private static int CompareIpAddresses(string? ip1, string? ip2)
    {
        if (ip1 == null && ip2 == null)
        {
            return 0;
        }
        if (ip1 == null)
        {
            return -1;
        }
        if (ip2 == null)
        {
            return 1;
        }

        if (IPAddress.TryParse(ip1, out IPAddress? addr1) &&
            IPAddress.TryParse(ip2, out IPAddress? addr2))
        {
            uint bytes1 = BitConverter.ToUInt32(addr1.GetAddressBytes().Reverse().ToArray(), 0);
            uint bytes2 = BitConverter.ToUInt32(addr2.GetAddressBytes().Reverse().ToArray(), 0);
            return bytes1.CompareTo(bytes2);
        }

        return string.Compare(ip1, ip2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts CIDR notation (e.g., 24 for /24) to subnet mask (e.g., 255.255.255.0)
    /// </summary>
    private static IPAddress CIDRToSubnetMask(int cidr)
    {
        if (cidr < 0 || cidr > 32)
        {
            throw new ArgumentException("CIDR must be between 0 and 32");
        }

        uint mask = (uint.MaxValue << (32 - cidr)) & 0xFFFFFFFF;
        byte[] bytes = BitConverter.GetBytes(mask).Reverse().ToArray();
        return new IPAddress(bytes);
    }

    private static int GetCIDR(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int bits = 0;
        foreach (byte b in bytes)
        {
            for (int i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0)
                {
                    bits++;
                }
                else
                {
                    return 32 - bits;
                }
            }
        }
        return 32;
    }

    private static IPAddress GetNetworkAddress(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] resultBytes = new byte[ipBytes.Length];

        for (int i = 0; i < ipBytes.Length; i++)
        {
            resultBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return new IPAddress(resultBytes);
    }

    private static IPAddress GetBroadcastAddress(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] resultBytes = new byte[ipBytes.Length];

        for (int i = 0; i < ipBytes.Length; i++)
        {
            resultBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(resultBytes);
    }

    private class DiscoveredPrinterInfo
    {
        public string IpAddress { get; set; } = string.Empty;
        public string Backend { get; set; } = string.Empty;
        public int? BackendPort { get; set; }
        public int? FrontendPort { get; set; }
        public string? FriendlyName { get; set; }
    }

    private static string FormatAsJSON(DiscoveredPrinterInfo[] printers, bool setDisabledByDefault)
    {
        var jsonPrinters = printers.Select(p => new
        {
            name = p.FriendlyName ?? $"{p.Backend}-{p.IpAddress}",
            ipAddress = p.IpAddress,
            backend = p.Backend,
            backendPort = p.BackendPort,
            frontendPort = p.FrontendPort,
            manufacturerName = "Unknown",
            modelName = "Unknown",
            notes = "Auto-discovered printer",
            isEnabled = !setDisabledByDefault
        }).ToArray();

        return JsonSerializer.Serialize(jsonPrinters, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatAsCSV(DiscoveredPrinterInfo[] printers, bool setDisabledByDefault)
    {
        StringBuilder csv = new StringBuilder();
        _ = csv.AppendLine("Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,IsEnabled");

        foreach (DiscoveredPrinterInfo printer in printers)
        {
            string name = printer.FriendlyName ?? $"{printer.Backend}-{printer.IpAddress}";
            string enabled = setDisabledByDefault ? "false" : "true";
            string backendPort = printer.BackendPort?.ToString() ?? "";
            string frontendPort = printer.FrontendPort?.ToString() ?? "";
            _ = csv.AppendLine($"\"{EscapeCsv(name)}\",\"{printer.IpAddress}\",\"{printer.Backend}\",{backendPort},{frontendPort},\"Unknown\",\"Unknown\",\"Auto-discovered\",{enabled}");
        }

        return csv.ToString();
    }

    private static string EscapeCsv(string value)
    {
        return value.Replace("\"", "\"\"");
    }

    private static async Task PrintStatusAsync(HttpClient http)
    {
        try
        {
            SetupStatus? status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
            if (status == null)
            {
#pragma warning disable CA1303
                Console.WriteLine("Status: <null response>");
#pragma warning restore CA1303
                return;
            }
            Console.WriteLine("needsSetup=" + status.NeedsSetup.ToString().ToLowerInvariant());
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error retrieving status: " + ex.Message);
        }
    }

    private static void PrintHelp()
    {
#pragma warning disable CA1303
        Console.WriteLine("PrintFarmer Admin CLI\n");
        Console.WriteLine("Commands:");
        Console.WriteLine("  --status                Show if initial setup is required");
        Console.WriteLine("  --username <value>      Admin username");
        Console.WriteLine("  --email <value>         Admin email");
        Console.WriteLine("  --password <value>      Admin password (min 12 chars)");
        Console.WriteLine("  --first-name <value>    First name (optional)");
        Console.WriteLine("  --last-name <value>     Last name (optional)");
        Console.WriteLine("  --base-url <url>        API base URL (default http://localhost:5245)");
        Console.WriteLine("  --help                  Display this help");
        Console.WriteLine();
        Console.WriteLine("Discovery & CSV Commands:");
        Console.WriteLine("  --sample-csv              Generate a sample CSV template with examples for all backends");
        Console.WriteLine("  --output <file>           Save sample CSV to file");
        Console.WriteLine("  --discover                Execute network discovery");
        Console.WriteLine("  --range <ranges>          CIDR ranges to scan (e.g., '192.168.1.0/24,10.0.0.0/24')");
        Console.WriteLine("  --interface <names>       Interface names to use (e.g., 'en0,eth0')");
        Console.WriteLine("  --timeout <ms>            Probe timeout in milliseconds (default: 200ms)");
        Console.WriteLine("  --concurrent <count>      Max concurrent scans (default: 10)");
        Console.WriteLine("  --format <json|csv>       Output format (default: json)");
        Console.WriteLine("  --no-approval             Set discovered printers to enabled=true (skip approval)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --status");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --username admin --email admin@example.com --password 'LongPassword123!'");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --sample-csv --output sample.csv");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --discover");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --discover --range '192.168.1.0/24' --format csv --output discovered.csv");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --discover --range '10.0.0.0/24' --timeout 200 --concurrent 20");
        Console.WriteLine("  dotnet run --project src/tools/AdminCli -- --discover  (uses saved config from previous run)");
#pragma warning restore CA1303
    }

    private static Dictionary<string, string> ParseArgs(string[] raw)
    {
        Dictionary<string, string> dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < raw.Length)
        {
            string token = raw[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            string key = token[2..];
            if (key.Length == 0)
            {
                i++;
                continue;
            }
            if (i + 1 < raw.Length && !raw[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                dic[key] = raw[i + 1];
                i += 2;
            }
            else
            {
                dic[key] = "true";
                i++;
            }
        }
        return dic;
    }

    private sealed record SetupStatus(bool NeedsSetup);
    private sealed record AuthResult(bool Success, string? Token, DateTime? ExpiresAt, object? User, string? Error);
    private sealed record DiscoveryConfig(string Range = "", string Interface = "", int Timeout = 200, int Concurrent = 10, string Format = "json", bool NoApproval = false);

    private static string GetConfigPath() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        ".admincli",
        "discovery-config.json");

    private static async Task SaveDiscoveryConfigAsync(DiscoveryConfig config)
    {
        try
        {
            string configPath = GetConfigPath();
            string? directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, json);
        }
        catch
        {
            // Silently fail - config persistence is optional
        }
    }

    private static async Task<DiscoveryConfig?> LoadDiscoveryConfigAsync()
    {
        try
        {
            string configPath = GetConfigPath();
            if (!File.Exists(configPath))
            {
                return null;
            }
            string json = await File.ReadAllTextAsync(configPath);
            return JsonSerializer.Deserialize<DiscoveryConfig>(json);
        }
        catch
        {
            return null; // Silently fail
        }
    }
}
