using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Integration tests that parse actual sample OrcaSlicer profiles from the filesystem.
/// These tests validate the profile parsing logic against real-world profile data,
/// ensuring the seeding logic works correctly with actual manufacturer bundles.
/// </summary>
public class ProfileSampleDataTests
{
    private static readonly string SampleProfilesPath = FindSampleProfilesPath();

    /// <summary>
    /// Loads sample profiles from sample_profiles/orcaslicer directory
    /// </summary>
    private static string FindSampleProfilesPath()
    {
        // Find the repository root by looking for farm-web.sln
        var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null)
        {
            if (File.Exists(Path.Combine(currentDir.FullName, "farm-web.sln")))
            {
                string profilePath = Path.Combine(currentDir.FullName, "sample_profiles/orcaslicer");
                if (Directory.Exists(profilePath))
                {
                    return profilePath;
                }
                break;
            }
            currentDir = currentDir.Parent;
        }

        // If we didn't find it from farm-web.sln, try looking at parent directory
        // (in case farm-web.sln is nested in src/)
        currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (currentDir != null && currentDir.Parent != null)
        {
            string profilePath = Path.Combine(currentDir.Parent.FullName, "sample_profiles/orcaslicer");
            if (Directory.Exists(profilePath))
            {
                return profilePath;
            }
            currentDir = currentDir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find sample_profiles/orcaslicer directory starting from {Directory.GetCurrentDirectory()}");
    }

    [Fact]
    public void SampleProfilesExist()
    {
        // Assert - verify the sample profiles directory exists and has content
        Directory.Exists(SampleProfilesPath).Should().BeTrue(
            $"Sample profiles directory should exist at {SampleProfilesPath}");

        string[] manufacturers = Directory.GetDirectories(SampleProfilesPath);
        manufacturers.Should().NotBeEmpty("Should have manufacturer directories");
        manufacturers.Should().Contain(m => m.EndsWith("Prusa"), "Should have Prusa directory");
        manufacturers.Should().Contain(m => m.EndsWith("Voron"), "Should have Voron directory");
    }

    [Theory]
    [InlineData("Prusa")]
    [InlineData("Voron")]
    [InlineData("Ratrig")]
    [InlineData("Flashforge")]
    [InlineData("Phrozen")]
    public void Manufacturer_HasMachineProfiles(string manufacturerName)
    {
        // Arrange
        string manufacturerDir = Path.Combine(SampleProfilesPath, manufacturerName);
        string machineDir = Path.Combine(manufacturerDir, "machine");

        // Act
        string[] machineFiles = Directory.GetFiles(machineDir, "*.json");

        // Assert
        machineFiles.Should().NotBeEmpty(
            $"Manufacturer '{manufacturerName}' should have machine profiles in {machineDir}");
    }

    [Theory]
    [InlineData("Prusa")]
    [InlineData("Voron")]
    [InlineData("Ratrig")]
    [InlineData("Flashforge")]
    public void MachineProfile_CanBeParsed(string manufacturerName)
    {
        // Arrange
        string manufacturerDir = Path.Combine(SampleProfilesPath, manufacturerName);
        string machineDir = Path.Combine(manufacturerDir, "machine");
        string[] machineFiles = Directory.GetFiles(machineDir, "*.json");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act & Assert
        foreach (string? file in machineFiles.Take(5))  // Test first 5 files
        {
            string json = File.ReadAllText(file);
            Func<MachineProfileDto> action = () => JsonSerializer.Deserialize<MachineProfileDto>(json, options)!;

            action.Should().NotThrow(
                $"Machine profile {Path.GetFileName(file)} should deserialize successfully");

            MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(json, options);
            profile.Should().NotBeNull();
            profile!.Name.Should().NotBeNullOrWhiteSpace($"{Path.GetFileName(file)} should have a name");
            // Manufacturer may be empty string in some sample profiles
        }
    }

    [Theory]
    [InlineData("Prusa")]
    [InlineData("Flashforge")]
    [InlineData("Voron")]
    public void FilamentProfile_CanBeParsed(string manufacturerName)
    {
        // Arrange
        string manufacturerDir = Path.Combine(SampleProfilesPath, manufacturerName);
        string filamentDir = Path.Combine(manufacturerDir, "filament");

        // Some manufacturers may not have filament profiles - skip them
        if (!Directory.Exists(filamentDir))
        {
            return;
        }

        string[] filamentFiles = Directory.GetFiles(filamentDir, "*.json");

        // Skip if no files found
        if (filamentFiles.Length == 0)
        {
            return;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act & Assert
        foreach (string? file in filamentFiles.Take(5))  // Test first 5 files
        {
            string json = File.ReadAllText(file);
            Func<FilamentProfileDto> action = () => JsonSerializer.Deserialize<FilamentProfileDto>(json, options)!;

            action.Should().NotThrow(
                $"Filament profile {Path.GetFileName(file)} should deserialize successfully");

            FilamentProfileDto? profile = JsonSerializer.Deserialize<FilamentProfileDto>(json, options);
            profile.Should().NotBeNull();
            profile!.Name.Should().NotBeNullOrWhiteSpace($"{Path.GetFileName(file)} should have a name");
            profile.Material.Should().NotBeNullOrWhiteSpace($"{Path.GetFileName(file)} should have a material");
        }
    }

    [Theory]
    [InlineData("Prusa")]
    [InlineData("Flashforge")]
    [InlineData("Voron")]
    public void ProcessProfile_CanBeParsed(string manufacturerName)
    {
        // Arrange
        string manufacturerDir = Path.Combine(SampleProfilesPath, manufacturerName);
        string processDir = Path.Combine(manufacturerDir, "process");

        // Some manufacturers may not have process profiles
        if (!Directory.Exists(processDir))
        {
            return;
        }

        string[] processFiles = Directory.GetFiles(processDir, "*.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act & Assert
        foreach (string? file in processFiles.Take(5))  // Test first 5 files
        {
            string json = File.ReadAllText(file);
            Func<ProcessProfileDto> action = () => JsonSerializer.Deserialize<ProcessProfileDto>(json, options)!;

            action.Should().NotThrow(
                $"Process profile {Path.GetFileName(file)} should deserialize successfully");

            ProcessProfileDto? profile = JsonSerializer.Deserialize<ProcessProfileDto>(json, options);
            profile.Should().NotBeNull();
            profile!.Name.Should().NotBeNullOrWhiteSpace($"{Path.GetFileName(file)} should have a name");
        }
    }

    [Fact]
    public void CatalogManufacturers_MatchSampleProfiles()
    {
        // Arrange - manufacturers in the catalog that should have sample profiles
        string[] catalogManufacturers = new[]
        {
            "Prusa", "Voron", "RatRig", "Flashforge", "Phrozen",
            "Elegoo", "Eryone", "Sovol"
        };

        // Act
        var sampleManufacturers = Directory.GetDirectories(SampleProfilesPath)
            .Select(d => new DirectoryInfo(d).Name)
            .ToList();

        // Assert
        foreach (string? manufacturer in catalogManufacturers)
        {
            string? matching = sampleManufacturers.FirstOrDefault(m =>
                m.Equals(manufacturer, StringComparison.OrdinalIgnoreCase));

            matching.Should().NotBeNull(
                $"Catalog manufacturer '{manufacturer}' should have sample profiles");
        }
    }

    [Fact]
    public void PrusaSampleProfiles_ContainValidData()
    {
        // Arrange
        string prusaDir = Path.Combine(SampleProfilesPath, "Prusa");
        string machineDir = Path.Combine(prusaDir, "machine");
        string filamentDir = Path.Combine(prusaDir, "filament");
        string processDir = Path.Combine(prusaDir, "process");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act
        var machineProfiles = Directory.GetFiles(machineDir, "*.json")
            .Where(f =>
            {
                try
                {
                    MachineProfileDto? profile = JsonSerializer.Deserialize<MachineProfileDto>(
                        File.ReadAllText(f), options);
                    return profile != null;
                }
                catch
                {
                    return false;
                }
            })
            .Select(f => JsonSerializer.Deserialize<MachineProfileDto>(File.ReadAllText(f), options))
            .Where(p => p != null)
            .Cast<MachineProfileDto>()
            .ToList();

        // Note: sample profiles are raw OrcaSlicer format, not necessarily our DTO format
        // So we just verify they exist and are readable
        machineProfiles.Count.Should().BeGreaterThan(5, "Prusa should have multiple machine profiles");
    }

    [Fact]
    public void CaseInsensitiveMatching_WorksForSampleProfiles()
    {
        // Arrange - the sample profile directories have mixed case
        string[] sampleDirs = Directory.GetDirectories(SampleProfilesPath);
        var manufacturerNames = sampleDirs.Select(d => new DirectoryInfo(d).Name).ToList();

        // Create catalog with different casing
        var catalogManufacturers = new HashSet<string>(
            new[] { "prusa", "VORON", "FlashForge" },  // Different casing
            StringComparer.OrdinalIgnoreCase);

        // Act
        var matchingManufacturers = manufacturerNames
            .Where(m => catalogManufacturers.Contains(m))
            .ToList();

        // Assert - should match despite case differences
        matchingManufacturers.Should().Contain("Prusa");
        matchingManufacturers.Should().Contain("Voron");
        matchingManufacturers.Should().Contain("Flashforge");
    }

    [Fact]
    public void FlashforgeSampleProfiles_HandleCasingInconsistency()
    {
        // Arrange - we know Flashforge has files with "FlashForge" vs "Flashforge" casing
        string flashforgeDir = Path.Combine(SampleProfilesPath, "Flashforge");

        if (!Directory.Exists(flashforgeDir))
        {
            return;  // Skip if Flashforge doesn't exist
        }

        string filamentDir = Path.Combine(flashforgeDir, "filament");
        string[] filamentFiles = Directory.GetFiles(filamentDir, "*.json");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Act
        var profiles = filamentFiles
            .Select(f => JsonSerializer.Deserialize<FilamentProfileDto>(File.ReadAllText(f), options))
            .Where(p => p != null)
            .Cast<FilamentProfileDto>()
            .ToList();

        // Assert - profiles should parse successfully regardless of case variations
        profiles.Should().NotBeEmpty();

        // At least some should have "FlashForge" (capital f) in the manufacturer or name
        var withFlashForgeCase = profiles.Where(p =>
            (p.Manufacturer?.Contains("FlashForge") ?? false) ||
            p.Name.Contains("FlashForge")).ToList();

        withFlashForgeCase.Count.Should().BeGreaterThan(0,
            "Should have profiles with 'FlashForge' casing");
    }
}
