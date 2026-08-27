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

    [Fact]
    public async Task ReconcileOverlayAsync_RemovedBundleWithConflictingOverlayPath_DoesNotBlockSiblingsAndRetriesLater()
    {
        // #2080 N-REC-1 review finding B1: the removal loop (unlike the ensure loop) was never
        // isolated per-bundle in the original fix, so a bundle being *removed* whose overlay
        // path is conflicted would abort reconciliation for every other bundle too.
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

        // Simulate the shared volume losing Alpha's custom bundle (e.g. removed by a sibling
        // worker) so Alpha now shows up as a *removed* bundle on the next reconcile, while its
        // stale overlay manifest link is also corrupted -- replaced with a plain file, exactly
        // as EnsureExpectedLink rejects with "overlay_path_conflict".
        Directory.Delete(Path.Join(customRoot, "Alpha"), recursive: true);
        File.Delete(Path.Join(customRoot, "Alpha.json"));
        File.Delete(alphaOverlayManifest);
        await File.WriteAllTextAsync(alphaOverlayManifest, "{}");

        Func<Task> firstReconcile = async () => await store.ReconcileOverlayAsync();
        _ = await firstReconcile.Should().NotThrowAsync(
            "a removed bundle's overlay conflict must not abort reconciliation for the others");

        // Beta was never touched, so it must be entirely unaffected by Alpha's removal conflict.
        new FileInfo(betaOverlayManifest).LinkTarget.Should().NotBeNull();
        File.ReadAllText(alphaOverlayManifest).Should().Be(
            "{}",
            "the conflicting removal must be skipped, not silently force-deleted");

        // The overlay directory link for Alpha was never touched by the conflicted manifest
        // removal (it aborts before reaching the directory removal), so it must still be the
        // dangling symlink from installation -- proving Alpha stayed tracked for retry instead
        // of being silently dropped from _knownCustomBundles.
        string alphaOverlayDirectory = Path.Join(overlayRoot, "Alpha");
        new DirectoryInfo(alphaOverlayDirectory).LinkTarget.Should().NotBeNull(
            "Alpha must remain tracked so its overlay artifacts are retried, not abandoned");

        // Once the conflict clears (e.g. an operator or a later successful pass removes the
        // stray file), a later reconcile must finish the delayed cleanup.
        File.Delete(alphaOverlayManifest);

        Func<Task> secondReconcile = async () => await store.ReconcileOverlayAsync();
        _ = await secondReconcile.Should().NotThrowAsync();

        File.Exists(alphaOverlayManifest).Should().BeFalse();
        Directory.Exists(alphaOverlayDirectory).Should().BeFalse(
            "the dangling overlay directory symlink must eventually be cleaned up once the " +
            "conflict is resolved -- it must not be leaked forever");
    }

    [Fact]
    public async Task ReconcileOverlayAsync_BundleThatFailsThenIsDeleted_RemainsTrackedUntilOverlayIsCleared()
    {
        // #2080 N-REC-1 review finding B2: replacing _knownCustomBundles with only the
        // successes from the ensure loop meant a bundle that succeeded once and later started
        // failing would silently fall out of tracking -- so if it was subsequently deleted from
        // disk while still broken, the removal loop would never even look at it again, leaking
        // its overlay link forever. Tracking must survive a transient ensure failure.
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

        string alphaOverlayManifest = Path.Join(overlayRoot, "Alpha.json");
        string alphaOverlayDirectory = Path.Join(overlayRoot, "Alpha");

        // Alpha starts failing: something replaces its overlay manifest symlink with a plain
        // file while the bundle itself is still present and otherwise healthy on disk.
        File.Delete(alphaOverlayManifest);
        await File.WriteAllTextAsync(alphaOverlayManifest, "{}");

        Func<Task> firstReconcile = async () => await store.ReconcileOverlayAsync();
        _ = await firstReconcile.Should().NotThrowAsync(
            "a bundle failing to ensure its overlay must not abort reconciliation");
        File.ReadAllText(alphaOverlayManifest).Should().Be(
            "{}",
            "the failing bundle must be skipped, not silently overwritten");

        // Now Alpha is actually deleted from the shared volume while still broken.
        Directory.Delete(Path.Join(customRoot, "Alpha"), recursive: true);
        File.Delete(Path.Join(customRoot, "Alpha.json"));

        Func<Task> secondReconcile = async () => await store.ReconcileOverlayAsync();
        _ = await secondReconcile.Should().NotThrowAsync();

        // The manifest removal is still blocked by the conflicting plain file, so the
        // directory-link removal in the same pass never runs -- Alpha must still be tracked,
        // proven by the dangling directory symlink surviving untouched.
        File.ReadAllText(alphaOverlayManifest).Should().Be("{}");
        new DirectoryInfo(alphaOverlayDirectory).LinkTarget.Should().NotBeNull(
            "Alpha must still be tracked for retry, not dropped the moment its ensure step failed");

        // The conflict finally clears; a later reconcile must complete the cleanup that would
        // otherwise have been abandoned forever by dropping Alpha from tracking too early.
        File.Delete(alphaOverlayManifest);

        Func<Task> thirdReconcile = async () => await store.ReconcileOverlayAsync();
        _ = await thirdReconcile.Should().NotThrowAsync();

        File.Exists(alphaOverlayManifest).Should().BeFalse();
        Directory.Exists(alphaOverlayDirectory).Should().BeFalse(
            "the overlay link must not be leaked forever just because the bundle failed once " +
            "before being deleted");
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
