using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

class ProfileParserTester
{
    static async Task Main(string[] args)
    {
        // Setup dependency injection
        var services = new ServiceCollection();
        services.AddLogging(builder => 
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        
        // Register a simple logging service
        services.AddSingleton<IUnifiedLoggingService>(sp => new NullLoggingService());
        
        // Register the OrcaProfilesService
        services.AddSingleton<OrcaProfilesService>();
        
        var serviceProvider = services.BuildServiceProvider();
        var profileService = serviceProvider.GetRequiredService<OrcaProfilesService>();

        try
        {
            Console.WriteLine("🔍 OrcaSlicer Profile Parser Test Harness");
            Console.WriteLine("=========================================\n");

            // List all available profiles using the actual service
            var machineProfiles = await profileService.ListAvailableMachineProfilesAsync();
            var filamentProfiles = await profileService.ListAvailableFilamentProfilesAsync();
            var processProfiles = await profileService.ListAvailableProcessProfilesAsync();

            Console.WriteLine($"📊 Profile Summary:");
            Console.WriteLine($"   Machine profiles:  {machineProfiles.Count}");
            Console.WriteLine($"   Filament profiles: {filamentProfiles.Count}");
            Console.WriteLine($"   Process profiles:  {processProfiles.Count}");
            Console.WriteLine($"   Total:             {machineProfiles.Count + filamentProfiles.Count + processProfiles.Count}\n");

            // Display machine profiles
            if (machineProfiles.Count > 0)
            {
                Console.WriteLine("🖨️  Machine Profiles:");
                Console.WriteLine(new string('-', 80));
                foreach (var profile in machineProfiles.Take(10))
                {
                    Console.WriteLine($"  • {profile.Name,-50} (Manufacturer: {profile.Manufacturer ?? "N/A"})");
                }
                if (machineProfiles.Count > 10)
                    Console.WriteLine($"  ... and {machineProfiles.Count - 10} more");
                Console.WriteLine();
            }

            // Display filament profiles
            if (filamentProfiles.Count > 0)
            {
                Console.WriteLine("🧵 Filament Profiles:");
                Console.WriteLine(new string('-', 80));
                foreach (var profile in filamentProfiles.Take(10))
                {
                    Console.WriteLine($"  • {profile.Name,-50} (Material: {profile.Material ?? "N/A"}, Nozzle: {profile.NozzleTemperature}°C, Bed: {profile.BedTemperature}°C)");
                }
                if (filamentProfiles.Count > 10)
                    Console.WriteLine($"  ... and {filamentProfiles.Count - 10} more");
                Console.WriteLine();
            }

            // Display process profiles
            if (processProfiles.Count > 0)
            {
                var withCompatible = processProfiles.Count(p => p.CompatiblePrinters?.Count > 0);
                Console.WriteLine($"Process Profiles: {processProfiles.Count} total, {withCompatible} with compatiblePrinters");
                
                // Show first few profiles
                foreach (var profile in processProfiles.Take(3))
                {
                    var compatibleCount = profile.CompatiblePrinters?.Count ?? 0;
                    Console.WriteLine($"  - {profile.Name}: {compatibleCount} compatible printers");
                }
            }

            // Test parsing specific profile if path provided
            if (args.Length > 0)
            {
                await TestSpecificProfileAsync(args[0]);
            }

            Console.WriteLine("✅ Profile parsing test completed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Environment.Exit(1);
        }
    }

    static async Task TestSpecificProfileAsync(string profilePath)
    {
        if (!File.Exists(profilePath))
        {
            Console.WriteLine($"❌ Profile file not found: {profilePath}");
            return;
        }

        Console.WriteLine($"\n📄 Testing specific profile: {profilePath}");
        Console.WriteLine(new string('-', 80));

        try
        {
            var json = await File.ReadAllTextAsync(profilePath);
            Console.WriteLine("✅ File read successfully");
            Console.WriteLine($"   File size: {json.Length} bytes");
            
            // Try to parse as JSON
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            Console.WriteLine($"✅ JSON parsed successfully");
            Console.WriteLine($"   Root element type: {root.ValueKind}");
            
            // Show top-level properties
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                Console.WriteLine("   Top-level properties:");
                foreach (var prop in root.EnumerateObject().Take(10))
                {
                    Console.WriteLine($"     - {prop.Name}: {prop.Value.ValueKind}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error testing profile: {ex.Message}");
        }
    }
}
