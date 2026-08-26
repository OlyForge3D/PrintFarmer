using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class CachedOrcaProfilesServiceReloadTests : IAsyncDisposable
{
    private readonly string _testRoot = Path.Join(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"profile-reload-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReloadProfilesAsync_AddedParent_EvictsAllCachesWithoutRestart()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "cache", "profiles.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfile(stockRoot, overlayRoot);

        await using var store = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRoot,
            customRoot);
        await using var service = new CachedOrcaProfilesService(
            NullLogger<CachedOrcaProfilesService>.Instance,
            overlayRoot,
            dbPath,
            customRoot);

        await store.InstallAsync(
            "Custom",
            Bundle(
                [
                    ("Micron 180 0.4 nozzle", "machine/micron.json"),
                ],
                [
                    new CustomProfileFileRequest(
                        "machine/micron.json",
                        "Micron 180",
                        Json("""
                            {
                              "name": "Micron 180 0.4 nozzle",
                              "inherits": "Micron 180 base",
                              "instantiation": "true",
                              "printer_model": "Voron 2.4 180",
                              "nozzle_diameter": ["0.4"]
                            }
                            """)),
                ]));

        ProfileReloadResult failedReload = await service.ReloadProfilesAsync();

        failedReload.Failures.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CustomProfileLoadFailure(
                "Custom",
                "Micron 180",
                "Micron 180 0.4 nozzle",
                "Micron 180 base"));
        (await service.GetMachineProfilesByPrinterModelAsync("Voron 2.4 180"))
            .Should().BeEmpty();

        await store.InstallAsync(
            "Custom",
            Bundle(
                [
                    ("Micron 180 base", "machine/base.json"),
                    ("Micron 180 0.4 nozzle", "machine/micron.json"),
                ],
                [
                    new CustomProfileFileRequest(
                        "machine/base.json",
                        "Micron 180",
                        Json("""
                            {
                              "name": "Micron 180 base",
                              "inherits": "Stock Parent",
                              "instantiation": "false",
                              "printable_height": "165"
                            }
                            """)),
                    new CustomProfileFileRequest(
                        "machine/micron.json",
                        "Micron 180",
                        Json("""
                            {
                              "name": "Micron 180 0.4 nozzle",
                              "inherits": "Micron 180 base",
                              "instantiation": "true",
                              "printer_model": "Voron 2.4 180",
                              "nozzle_diameter": ["0.4"]
                            }
                            """)),
                ]));

        ProfileReloadResult successfulReload =
            await service.ReloadProfilesAsync();
        var profiles = await service.GetMachineProfilesByPrinterModelAsync(
            "Voron 2.4 180");

        successfulReload.Failures.Should().BeEmpty();
        profiles.Should().ContainSingle();
        profiles[0].GcodeDialect.Should().Be("klipper");
        profiles[0].BuildVolumeZ.Should().Be(165);
    }

    [Fact]
    public async Task InstallAsync_StockBundleName_RejectsOverwrite()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        await File.WriteAllTextAsync(
            Path.Join(stockRoot, "Voron.json"),
            "{}");
        await using var store = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRoot,
            customRoot);

        Func<Task> act = () => store.InstallAsync(
            "Voron",
            Bundle([], []));

        await act.Should().ThrowAsync<CustomProfileBundleException>()
            .Where(exception =>
                exception.Code == "stock_bundle_conflict");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static CustomProfileBundleRequest Bundle(
        (string Name, string SubPath)[] entries,
        CustomProfileFileRequest[] files)
    {
        string machineEntries = string.Join(
            ',',
            entries.Select(entry =>
                $$"""{"name":"{{entry.Name}}","sub_path":"{{entry.SubPath}}"}"""));
        return new CustomProfileBundleRequest(
            Json(
                $$"""
                  {
                    "name": "Custom",
                    "machine_model_list": [],
                    "machine_list": [{{machineEntries}}],
                    "filament_list": [],
                    "process_list": []
                  }
                  """),
            files);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void WriteStockProfile(
        string stockRoot,
        string overlayRoot)
    {
        string stockDirectory = Path.Join(stockRoot, "Stock", "machine");
        Directory.CreateDirectory(stockDirectory);
        File.WriteAllText(
            Path.Join(stockRoot, "Stock.json"),
            """
            {
              "name": "Stock",
              "machine_model_list": [],
              "machine_list": [
                {
                  "name": "Stock Parent",
                  "sub_path": "machine/Stock Parent.json"
                }
              ],
              "filament_list": [],
              "process_list": []
            }
            """);
        File.WriteAllText(
            Path.Join(stockDirectory, "Stock Parent.json"),
            """
            {
              "name": "Stock Parent",
              "instantiation": "true",
              "printer_model": "Stock Model",
              "nozzle_diameter": ["0.4"],
              "gcode_flavor": "klipper"
            }
            """);
        _ = File.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock.json"),
            Path.Join(stockRoot, "Stock.json"));
        _ = Directory.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock"),
            Path.Join(stockRoot, "Stock"));
    }
}
