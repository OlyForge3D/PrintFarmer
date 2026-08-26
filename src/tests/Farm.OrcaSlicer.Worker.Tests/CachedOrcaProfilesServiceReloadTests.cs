using System.Text.Json;
using Farm.OrcaSlicer.Worker.Controllers;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(".install-example")]
    [InlineData(".INSTALL-example")]
    [InlineData(".backup-example")]
    [InlineData(".BACKUP-example")]
    [InlineData(".printfarmer")]
    [InlineData(".PRINTFARMER")]
    public async Task InstallAsync_ReservedBundleName_RejectsWithoutSideEffects(
        string bundleName)
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        string sentinelPath = Path.Join(_testRoot, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "unchanged");
        await using var store = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRoot,
            customRoot);

        Func<Task> act = () => store.InstallAsync(
            bundleName,
            Bundle([], []));

        await act.Should().ThrowAsync<CustomProfileBundleException>()
            .Where(exception =>
                exception.Code == "invalid_bundle_name");
        (await File.ReadAllTextAsync(sentinelPath)).Should().Be("unchanged");
        Directory.EnumerateFileSystemEntries(customRoot).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(overlayRoot).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../etc/foo.json")]
    [InlineData("/etc/foo.json")]
    [InlineData("C:/etc/foo.json")]
    [InlineData(@"C:\etc\foo.json")]
    public async Task InstallAsync_UnsafeRelativePath_RejectsPath(
        string relativePath)
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        string escapedPath = Path.Join(_testRoot, "etc", "foo.json");
        await using var store = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRoot,
            customRoot);

        Func<Task> act = () => store.InstallAsync(
            "Custom",
            Bundle(
                [],
                [
                    new CustomProfileFileRequest(
                        relativePath,
                        "Family",
                        Json("""{"name":"Escaped"}""")),
                ]));

        await act.Should().ThrowAsync<CustomProfileBundleException>()
            .Where(exception =>
                exception.Code == "invalid_profile_path");
        File.Exists(escapedPath).Should().BeFalse();
    }

    [Fact]
    public async Task MutateAndReloadProfilesAsync_ConcurrentRead_WaitsForWriter()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        string dbPath = Path.Join(_testRoot, "cache", "profiles.db");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);
        WriteStockProfile(stockRoot, overlayRoot);
        await using var service = new CachedOrcaProfilesService(
            NullLogger<CachedOrcaProfilesService>.Instance,
            overlayRoot,
            dbPath,
            customRoot);
        _ = await service.ListAvailableMachineProfilesAsync();

        TaskCompletionSource mutationEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseMutation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task mutation = service.MutateAndReloadProfilesAsync(
            async cancellationToken =>
            {
                mutationEntered.SetResult();
                await releaseMutation.Task.WaitAsync(cancellationToken);
                return true;
            });
        await mutationEntered.Task;

        var read = service.ListAvailableMachineProfilesAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        read.IsCompleted.Should().BeFalse();
        releaseMutation.SetResult();
        await mutation;
        (await read).Should().ContainSingle(
            profile => profile.Name == "Stock Parent");
    }

    [Fact]
    public async Task ReconciliationPoll_SharedVolume_LoadsSiblingBundle()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRootA = Path.Join(_testRoot, "overlay-a");
        string overlayRootB = Path.Join(_testRoot, "overlay-b");
        string customRoot = Path.Join(_testRoot, "custom");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRootA);
        Directory.CreateDirectory(overlayRootB);
        Directory.CreateDirectory(customRoot);
        WriteStockProfile(stockRoot, overlayRootA);
        LinkStockProfile(stockRoot, overlayRootB);

        await using var storeA = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRootA,
            customRoot);
        await using var storeB = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRootB,
            customRoot);
        await using var serviceA = new CachedOrcaProfilesService(
            NullLogger<CachedOrcaProfilesService>.Instance,
            overlayRootA,
            Path.Join(_testRoot, "cache-a", "profiles.db"),
            customRoot);
        await using var serviceB = new CachedOrcaProfilesService(
            NullLogger<CachedOrcaProfilesService>.Instance,
            overlayRootB,
            Path.Join(_testRoot, "cache-b", "profiles.db"),
            customRoot);
        _ = await serviceA.ListAvailableMachineProfilesAsync();
        CustomProfilesReconciliationState stateB = new();
        using CustomProfilesReconciliationService reconciliationB = new(
            storeB,
            serviceB,
            stateB,
            new ConfigurationBuilder().Build(),
            NullLogger<CustomProfilesReconciliationService>.Instance);
        await reconciliationB.CheckForChangesAsync(CancellationToken.None);
        stateB.IsReady.Should().BeTrue();

        _ = await serviceA.MutateAndReloadProfilesAsync(
            async cancellationToken =>
            {
                await storeA.InstallAsync(
                    "Custom",
                    CompleteBundle(),
                    cancellationToken);
                return true;
            });
        await reconciliationB.CheckForChangesAsync(CancellationToken.None);
        stateB.IsReady.Should().BeFalse();
        Directory.Exists(Path.Join(overlayRootB, "Custom"))
            .Should().BeFalse();

        await reconciliationB.CheckForChangesAsync(CancellationToken.None);

        stateB.IsReady.Should().BeTrue();
        (await serviceB.GetMachineProfilesByPrinterModelAsync(
            "Voron 2.4 180"))
            .Should().ContainSingle()
            .Which.Name.Should().Be("Micron 180 0.4 nozzle");
    }

    [Fact]
    public async Task InstallAsync_MissingParent_RollsBackRejectedBundle()
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
        CustomProfilesReconciliationState state = new();
        CustomProfilesController controller = new(
            store,
            service,
            state,
            NullLogger<CustomProfilesController>.Instance);

        ActionResult<CustomProfileMutationResponse> result =
            await controller.InstallAsync(
                "Broken",
                Bundle(
                    [("Broken Child", "machine/broken.json")],
                    [
                        new CustomProfileFileRequest(
                            "machine/broken.json",
                            "Broken Family",
                            Json("""
                                {
                                  "name": "Broken Child",
                                  "inherits": "Unavailable Parent",
                                  "instantiation": "true",
                                  "printer_model": "Broken Model",
                                  "nozzle_diameter": ["0.4"]
                                }
                                """)),
                    ]),
                CancellationToken.None);

        result.Result.Should()
            .BeOfType<UnprocessableEntityObjectResult>();
        File.Exists(Path.Join(customRoot, "Broken.json")).Should().BeFalse();
        Directory.Exists(Path.Join(customRoot, "Broken")).Should().BeFalse();
        Directory.EnumerateFileSystemEntries(overlayRoot)
            .Select(Path.GetFileName)
            .Should().NotContain(["Broken.json", "Broken"]);
        state.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task InstallAsync_UnrelatedBrokenBundle_InstallsHealthyBundle()
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
            "Broken",
            Bundle(
                [("Broken Child", "machine/broken.json")],
                [
                    new CustomProfileFileRequest(
                        "machine/broken.json",
                        "Broken Family",
                        Json("""
                            {
                              "name": "Broken Child",
                              "inherits": "Unavailable Parent",
                              "instantiation": "true",
                              "printer_model": "Broken Model",
                              "nozzle_diameter": ["0.4"]
                            }
                            """)),
                ]));
        ProfileReloadResult brokenReload =
            await service.ReloadProfilesAsync();
        brokenReload.Failures.Should().ContainSingle()
            .Which.BundleName.Should().Be("Broken");
        CustomProfilesReconciliationState state = new();
        CustomProfilesController controller = new(
            store,
            service,
            state,
            NullLogger<CustomProfilesController>.Instance);

        ActionResult<CustomProfileMutationResponse> result =
            await controller.InstallAsync(
                "Healthy",
                CompleteBundle(),
                CancellationToken.None);

        OkObjectResult ok = result.Result.Should()
            .BeOfType<OkObjectResult>().Subject;
        CustomProfileMutationResponse response = ok.Value.Should()
            .BeOfType<CustomProfileMutationResponse>().Subject;
        response.Failures.Should().Contain(
            failure => failure.BundleName == "Broken");
        File.Exists(Path.Join(customRoot, "Healthy.json"))
            .Should().BeTrue();
        Directory.Exists(Path.Join(customRoot, "Healthy"))
            .Should().BeTrue();
        File.Exists(Path.Join(overlayRoot, "Healthy.json"))
            .Should().BeTrue();
        Directory.Exists(Path.Join(overlayRoot, "Healthy"))
            .Should().BeTrue();
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
        LinkStockProfile(stockRoot, overlayRoot);
    }

    private static void LinkStockProfile(
        string stockRoot,
        string overlayRoot)
    {
        _ = File.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock.json"),
            Path.Join(stockRoot, "Stock.json"));
        _ = Directory.CreateSymbolicLink(
            Path.Join(overlayRoot, "Stock"),
            Path.Join(stockRoot, "Stock"));
    }

    private static CustomProfileBundleRequest CompleteBundle() =>
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
            ]);
}
