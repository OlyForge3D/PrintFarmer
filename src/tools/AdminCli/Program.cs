using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Globalization;
using Farm.Shared.Discovery;

namespace Farm.Tools.AdminCli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
#pragma warning disable CA1303 // Suppress localization warnings for CLI output
        var argsDic = ParseArgs(args);
        if (argsDic.ContainsKey("help") || argsDic.Count == 0)
        {
            PrintHelp();
            return 0;
        }

        // Load saved discovery config and merge with CLI args (CLI args take precedence)
        if (argsDic.ContainsKey("discover") && !argsDic.ContainsKey("range"))
        {
            var savedConfig = await LoadDiscoveryConfigAsync();
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

        var baseUrl = argsDic.GetValueOrDefault("base-url", "http://localhost:5245").TrimEnd('/');
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

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

        var hasUsername = argsDic.TryGetValue("username", out var _);
        var hasEmail = argsDic.TryGetValue("email", out var _);
        var hasPassword = argsDic.TryGetValue("password", out var _);
        if (!hasUsername || !hasEmail || !hasPassword)
        {
            await Console.Error.WriteLineAsync("ERROR: --username, --email and --password are required unless using --status, --discover, or --sample-csv.");
            PrintHelp();
            return 1;
        }

        var password = argsDic["password"];
        if (password.Length < 12)
        {
            await Console.Error.WriteLineAsync("ERROR: Password must be at least 12 characters (server requirement).");
            return 2;
        }

        Console.WriteLine($"[AdminCli] Attempting initial admin creation against {baseUrl} ...");

        var status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
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
            username = argsDic["username"],
            email = argsDic["email"],
            password,
            firstName = argsDic.GetValueOrDefault("first-name", "Admin"),
            lastName = argsDic.GetValueOrDefault("last-name", "User")
        };

        var response = await http.PostAsJsonAsync("/api/setup/initial-admin", payload, Json);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync($"Server returned {(int)response.StatusCode} {response.StatusCode}\n{body}");
            return 4;
        }

        try
        {
            var result = JsonSerializer.Deserialize<AuthResult>(body, Json);
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
            var outputFile = argsDic.GetValueOrDefault("output", "");
            string csv = GenerateSampleCsv();

            if (!string.IsNullOrWhiteSpace(outputFile))
            {
                await System.IO.File.WriteAllTextAsync(outputFile, csv);
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
        var csv = new StringBuilder();
        csv.AppendLine("Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,IsEnabled");

        // Example rows for each backend type
        csv.AppendLine("\"Moonraker Printer\",\"192.168.1.100\",\"Moonraker\",\"7125\",\"80\",\"Creality\",\"Ender-3 Max\",\"Main production printer\",\"false\"");
        csv.AppendLine("\"PrusaLink Printer\",\"192.168.1.101\",\"PrusaLink\",\"80\",\"443\",\"Prusa\",\"MK3S+\",\"High precision prints\",\"false\"");
        csv.AppendLine("\"SDCP Printer\",\"192.168.1.102\",\"SDCP\",\"80\",\"80\",\"Bambu Lab\",\"X1 Carbon\",\"Fast prints\",\"false\"");
        csv.AppendLine("\"OctoPrint Printer\",\"192.168.1.103\",\"Moonraker\",\"7125\",\"80\",\"Anet\",\"A8 Plus\",\"Legacy setup with OctoPrint\",\"false\"");

        return csv.ToString();
    }

    private static async Task<int> HandleDiscoveryAsync(Dictionary<string, string> argsDic)
    {
#pragma warning disable CA1303
        try
        {
            var outputFormat = argsDic.GetValueOrDefault("format", "json").ToLowerInvariant();
            var outputFile = argsDic.GetValueOrDefault("output", "");
            var noApproval = argsDic.ContainsKey("no-approval");
            var rangeConstraints = argsDic.GetValueOrDefault("range", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).ToList();
            var interfaceConstraints = argsDic.GetValueOrDefault("interface", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).ToList();
            var probeTimeoutMs = int.TryParse(argsDic.GetValueOrDefault("timeout", "200"), out var t) ? t : 200;
            var maxConcurrentScans = int.TryParse(argsDic.GetValueOrDefault("concurrent", "10"), out var c) ? c : 10;

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
            var probes = new INetworkDiscoveryProbe[]
            {
                new MoonrakerDiscoveryProbe(),
                new PrusaLinkDiscoveryProbe(),
                new OctoPrintDiscoveryProbe(),
                new SdcpDiscoveryProbe()
            };

            // Perform local discovery without API dependency
            var discovered = await PerformLocalDiscoveryAsync(probes, rangeConstraints, interfaceConstraints, probeTimeoutMs, maxConcurrentScans);

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
                await System.IO.File.WriteAllTextAsync(outputFile, formattedOutput);
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
        
        var discovered = new List<DiscoveredPrinterInfo>();

        // Get local network interfaces
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                        i.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
            .ToList();

        // Apply interface filter if specified
        if (interfaceConstraints.Count > 0)
        {
            interfaces = interfaces.Where(i => interfaceConstraints.Contains(i.Name)).ToList();
            if (interfaces.Count == 0)
            {
                Console.WriteLine($"[Discovery] No matching interfaces found. Available: {string.Join(", ", System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(i => i.Name))}");
                return discovered;
            }
        }

        Console.WriteLine($"\n[Discovery] Found {interfaces.Count} active network interface(s)\n");

        foreach (var iface in interfaces)
        {
            var ipProps = iface.GetIPProperties();
            foreach (var unicast in ipProps.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            {
                var ip = unicast.Address;
                var subnet = unicast.IPv4Mask;

                // Calculate network range from IP and subnet mask
                var network = GetNetworkAddress(ip, subnet);
                var broadcast = GetBroadcastAddress(ip, subnet);
                var cidr = $"{network}/{GetCIDR(subnet)}";

                // Check if this range matches any constraints
                if (rangeConstraints.Count > 0)
                {
                    var matchesConstraint = rangeConstraints.Any(constraint =>
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
                var scanStart = network;
                var scanEnd = broadcast;
                
                if (rangeConstraints.Count > 0)
                {
                    // Find the tightest (smallest) constraint that applies to this interface
                    var applicableConstraint = rangeConstraints.FirstOrDefault(constraint =>
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
                        var parts = applicableConstraint.Split('/');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var constraintCidr))
                        {
                            var constraintIp = System.Net.IPAddress.Parse(parts[0]);
                            var constraintSubnet = CIDRToSubnetMask(constraintCidr);
                            scanStart = GetNetworkAddress(constraintIp, constraintSubnet);
                            scanEnd = GetBroadcastAddress(constraintIp, constraintSubnet);
                            Console.WriteLine($"  Constraint applied: {scanStart}/{constraintCidr} → {scanEnd}");
                        }
                    }
                }

                // Scan IP range with concurrency
                var start = BitConverter.ToUInt32(scanStart.GetAddressBytes().Reverse().ToArray(), 0);
                var end = BitConverter.ToUInt32(scanEnd.GetAddressBytes().Reverse().ToArray(), 0);
                var total = end - start;
                var scanCount = 0;

                using var semaphore = new System.Threading.SemaphoreSlim(maxConcurrentScans, maxConcurrentScans);
                var scanTasks = new List<Task>();

                for (uint i = start + 1; i < end; i++)
                {
                    var ipValue = i; // Capture for closure
                    var task = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var scanCountLocal = Interlocked.Increment(ref scanCount);
                            
                            // Show progress every 10 IPs scanned
                            if (scanCountLocal % 10 == 0 || scanCountLocal == 1)
                            {
                                var progress = (int)((scanCountLocal / (double)total) * 100);
                                Console.Write($"\r  Scanning... [{progress}%] ({scanCountLocal}/{total} IPs checked, {discovered.Count} found)");
                            }

                            var ipBytes = BitConverter.GetBytes(ipValue).Reverse().ToArray();
                            var targetIp = new System.Net.IPAddress(ipBytes).ToString();

                            // Probe with each discovery probe and collect all results
                            List<ProbeResult> probeResults = new();
                            foreach (var probe in probes)
                            {
                                try
                                {
                                    var result = await probe.ProbeAsync(targetIp, probeTimeoutMs, CancellationToken.None);
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
                                var bestResult = probeResults.MaxBy(r => r.ConfidenceScore)!;
                                var result = bestResult.Printer;
                                
                                var printerInfo = new DiscoveredPrinterInfo
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
                            semaphore.Release();
                        }
                    });
                    scanTasks.Add(task);
                }

                // Wait for all scans to complete
                await Task.WhenAll(scanTasks);
                Console.WriteLine($"\r  Scan complete. Checked {scanCount} IPs.\n");
            }
        }

        return discovered;
    }

    /// <summary>
    /// Checks if a constraint range (CIDR) contains the target range.
    /// </summary>
    private static bool IpRangeContainsRange(string cidrConstraint, System.Net.IPAddress targetNetwork, System.Net.IPAddress targetBroadcast)
    {
        var parts = cidrConstraint.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var constraintCidr))
        {
            throw new ArgumentException($"Invalid CIDR format: {cidrConstraint}");
        }

        var constraintIp = System.Net.IPAddress.Parse(parts[0]);
        var constraintSubnet = CIDRToSubnetMask(constraintCidr);
        var constraintNetwork = GetNetworkAddress(constraintIp, constraintSubnet);
        var constraintBroadcast = GetBroadcastAddress(constraintIp, constraintSubnet);

        var targetStart = BitConverter.ToUInt32(targetNetwork.GetAddressBytes().Reverse().ToArray(), 0);
        var targetEnd = BitConverter.ToUInt32(targetBroadcast.GetAddressBytes().Reverse().ToArray(), 0);
        var constraintStart = BitConverter.ToUInt32(constraintNetwork.GetAddressBytes().Reverse().ToArray(), 0);
        var constraintEnd = BitConverter.ToUInt32(constraintBroadcast.GetAddressBytes().Reverse().ToArray(), 0);

        // Check if constraint overlaps with or is contained in target range
        // Returns true if: constraint is fully within target, or they overlap, or target is fully within constraint
        return !(constraintEnd < targetStart || constraintStart > targetEnd);
    }

    /// <summary>
    /// Converts CIDR notation (e.g., 24 for /24) to subnet mask (e.g., 255.255.255.0)
    /// </summary>
    private static System.Net.IPAddress CIDRToSubnetMask(int cidr)
    {
        if (cidr < 0 || cidr > 32)
        {
            throw new ArgumentException("CIDR must be between 0 and 32");
        }
        
        var mask = (uint.MaxValue << (32 - cidr)) & 0xFFFFFFFF;
        var bytes = BitConverter.GetBytes(mask).Reverse().ToArray();
        return new System.Net.IPAddress(bytes);
    }

    private static int GetCIDR(System.Net.IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
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

    private static System.Net.IPAddress GetNetworkAddress(System.Net.IPAddress ip, System.Net.IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var resultBytes = new byte[ipBytes.Length];

        for (int i = 0; i < ipBytes.Length; i++)
        {
            resultBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
        }

        return new System.Net.IPAddress(resultBytes);
    }

    private static System.Net.IPAddress GetBroadcastAddress(System.Net.IPAddress ip, System.Net.IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        var resultBytes = new byte[ipBytes.Length];

        for (int i = 0; i < ipBytes.Length; i++)
        {
            resultBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
        }

        return new System.Net.IPAddress(resultBytes);
    }

    private class DiscoveredPrinterInfo
    {
        public string IpAddress { get; set; } = string.Empty;
        public string Backend { get; set; } = string.Empty;
        public int Port { get; set; }
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
        var csv = new StringBuilder();
        csv.AppendLine("Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,IsEnabled");

        foreach (var printer in printers)
        {
            var name = printer.FriendlyName ?? $"{printer.Backend}-{printer.IpAddress}";
            var enabled = setDisabledByDefault ? "false" : "true";
            var backendPort = printer.BackendPort?.ToString() ?? "";
            var frontendPort = printer.FrontendPort?.ToString() ?? "";
            csv.AppendLine($"\"{EscapeCsv(name)}\",\"{printer.IpAddress}\",\"{printer.Backend}\",{backendPort},{frontendPort},\"Unknown\",\"Unknown\",\"Auto-discovered\",{enabled}");
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
            var status = await http.GetFromJsonAsync<SetupStatus>("/api/setup/status");
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
        var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < raw.Length)
        {
            var token = raw[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            var key = token[2..];
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

    private static string GetConfigPath() => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        ".admincli",
        "discovery-config.json");

    private static async Task SaveDiscoveryConfigAsync(DiscoveryConfig config)
    {
        try
        {
            var configPath = GetConfigPath();
            var directory = System.IO.Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(configPath, json);
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
            var configPath = GetConfigPath();
            if (!System.IO.File.Exists(configPath))
            {
                return null;
            }
            var json = await System.IO.File.ReadAllTextAsync(configPath);
            return JsonSerializer.Deserialize<DiscoveryConfig>(json);
        }
        catch
        {
            return null; // Silently fail
        }
    }
}
