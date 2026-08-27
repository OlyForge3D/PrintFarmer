using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// #2080 N-REC-1: <see cref="CustomProfileBundleStore.ReconcileOverlayAsync"/> must isolate
/// per-bundle overlay failures so one malformed bundle cannot block reconciliation for its
/// siblings.
/// </summary>
public sealed class CustomProfileBundleStoreReconciliationTests : IAsyncDisposable
{
    private readonly string _testRoot = Path.Join(
        AppContext.BaseDirectory,
        "test-artifacts",
        $"bundle-reconcile-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReconcileOverlayAsync_OneBundleWithConflictingOverlayPath_DoesNotBlockOthers()
    {
        string stockRoot = Path.Join(_testRoot, "stock");
        string overlayRoot = Path.Join(_testRoot, "overlay");
        string customRoot = Path.Join(_testRoot, "custom");
        Directory.CreateDirectory(stockRoot);
        Directory.CreateDirectory(overlayRoot);
        Directory.CreateDirectory(customRoot);

        await using var store = new CustomProfileBundleStore(
            NullLogger<CustomProfileBundleStore>.Instance,
            stockRoot,
            overlayRoot,
            customRoot);

        await store.InstallAsync("Alpha", Bundle("Alpha Model"));
        await store.InstallAsync("Beta", Bundle("Beta Model"));

        string alphaOverlayManifest = Path.Join(overlayRoot, "Alpha.json");
        string betaOverlayManifest = Path.Join(overlayRoot, "Beta.json");
        File.Exists(alphaOverlayManifest).Should().BeTrue();
        File.Exists(betaOverlayManifest).Should().BeTrue();

        // Simulate a sibling worker (or manual intervention) replacing Alpha's overlay
        // manifest symlink with a plain file -- this is exactly what
        // EnsureExpectedLink rejects with "overlay_path_conflict".
        File.Delete(alphaOverlayManifest);
        await File.WriteAllTextAsync(alphaOverlayManifest, "{}");

        Func<Task> reconcile = async () => await store.ReconcileOverlayAsync();

        _ = await reconcile.Should().NotThrowAsync(
            "one bundle's overlay conflict must not abort reconciliation for the others");

        // Beta was never touched by the corruption, so its overlay link must remain the
        // expected symlink to its custom manifest -- reconciliation must not have
        // regressed it while working around Alpha's failure.
        new FileInfo(betaOverlayManifest).LinkTarget.Should().NotBeNull();
        File.ReadAllText(alphaOverlayManifest).Should().Be(
            "{}",
            "the conflicting bundle must be skipped, not silently overwritten");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private static CustomProfileBundleRequest Bundle(string machineModelName) =>
        new(
            Json(
                $$"""
                  {
                    "name": "Custom",
                    "machine_model_list": [],
                    "machine_list": [{"name":"{{machineModelName}}","sub_path":"machine/model.json"}],
                    "filament_list": [],
                    "process_list": []
                  }
                  """),
            [
                new CustomProfileFileRequest(
                    "machine/model.json",
                    machineModelName,
                    Json($$"""
                        {
                          "name": "{{machineModelName}}",
                          "instantiation": "false"
                        }
                        """)),
            ]);

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
