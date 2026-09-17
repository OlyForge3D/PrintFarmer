using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class VerifiedReleaseManifestBindingStoreTests
{
    private const string ReleaseId = "stable:1.2.3";
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task EnsureBoundAsync_SameProcessChangedDigest_RejectsConflict()
    {
        InMemorySettingsRepository repository = new();
        var store = new VerifiedReleaseManifestBindingStore(repository);
        await store.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);

        Func<Task> act = () => store.EnsureBoundAsync(
            ReleaseId,
            "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*digest conflict*");
    }

    [Fact]
    public async Task EnsureBoundAsync_RecreatedStore_ReloadsAndEnforcesPersistedBinding()
    {
        InMemorySettingsRepository repository = new();
        await new VerifiedReleaseManifestBindingStore(repository)
            .EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);
        var reloadedStore = new VerifiedReleaseManifestBindingStore(repository);

        await reloadedStore.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);
        Func<Task> changed = () => reloadedStore.EnsureBoundAsync(
            ReleaseId,
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            CancellationToken.None);

        await changed.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*digest conflict*");
        repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureBoundAsync_ConcurrentDifferentDigests_FirstWriterWinsAndConflictRereadsPersistedBinding()
    {
        InMemorySettingsRepository repository = new();
        var first = new VerifiedReleaseManifestBindingStore(repository);
        var second = new VerifiedReleaseManifestBindingStore(repository);
        Task<Exception?> firstTask = Task.Run(() => Record.ExceptionAsync(() => first.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None)));
        Task<Exception?> secondTask = Task.Run(() => Record.ExceptionAsync(() => second.EnsureBoundAsync(
            ReleaseId,
            "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            CancellationToken.None)));

        Exception?[] errors = await Task.WhenAll(firstTask, secondTask);
        Exception? firstError = errors[0];
        Exception? secondError = errors[1];

        Assert.True(firstError is null ^ secondError is null);
        Assert.Contains("digest conflict", (firstError ?? secondError)!.Message);
        repository.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureBoundAsync_ProviderDbExceptionOnRead_ReportsSubsystemUnavailable()
    {
        FailingSettingsRepository repository = new(new SqliteException("database is locked", 5), failOnRead: true);
        var store = new VerifiedReleaseManifestBindingStore(repository);

        Func<Task> act = () => store.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);

        (await act.Should().ThrowAsync<HostUpdateSubsystemUnavailableException>())
            .Which.Code.Should().Be(HostUpdateAvailabilityCodes.ManifestBindingDatabaseUnavailable);
    }

    [Fact]
    public async Task EnsureBoundAsync_ProviderDbExceptionOnCreate_ReportsSubsystemUnavailable()
    {
        FailingSettingsRepository repository = new(new SqliteException("disk I/O error", 10), failOnRead: false);
        var store = new VerifiedReleaseManifestBindingStore(repository);

        Func<Task> act = () => store.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);

        (await act.Should().ThrowAsync<HostUpdateSubsystemUnavailableException>())
            .Which.Code.Should().Be(HostUpdateAvailabilityCodes.ManifestBindingDatabaseUnavailable);
    }

    [Fact]
    public async Task EnsureBoundAsync_Cancellation_IsNotReclassifiedAsUnavailable()
    {
        FailingSettingsRepository repository = new(new OperationCanceledException(), failOnRead: true);
        var store = new VerifiedReleaseManifestBindingStore(repository);

        Func<Task> act = () => store.EnsureBoundAsync(ReleaseId, Digest, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FailingSettingsRepository(Exception failure, bool failOnRead) : IAppSettingsRepository
    {
        public Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default) => throw failure;

        public Task<AppSettingsEntity?> GetReadOnlyAsync(string key, CancellationToken ct = default) =>
            failOnRead ? throw failure : Task.FromResult<AppSettingsEntity?>(null);

        public Task SetAsync(string key, string value, CancellationToken ct = default) => throw failure;

        public Task<bool> TryCreateAsync(string key, string value, CancellationToken ct = default) => throw failure;

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => throw failure;

        public Task SaveChangesAsync(CancellationToken ct = default) => throw failure;
    }

    private sealed class InMemorySettingsRepository : IAppSettingsRepository
    {
        private readonly Dictionary<string, AppSettingsEntity> _entries = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public int SaveCount { get; private set; }

        public Task<AppSettingsEntity?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.GetValueOrDefault(key));

        public Task<AppSettingsEntity?> GetReadOnlyAsync(string key, CancellationToken ct = default)
        {
            AppSettingsEntity? entity = _entries.GetValueOrDefault(key);
            return Task.FromResult(entity is null
                ? null
                : new AppSettingsEntity
                {
                    Id = entity.Id,
                    Key = entity.Key,
                    SettingsJson = entity.SettingsJson,
                    UpdatedAt = entity.UpdatedAt,
                });
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _entries[key] = new AppSettingsEntity
                {
                    Id = _entries.Count + 1,
                    Key = key,
                    SettingsJson = value,
                    UpdatedAt = DateTime.UtcNow,
                };
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(string key, string value, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_entries.ContainsKey(key))
                {
                    return Task.FromResult(false);
                }

                _entries[key] = new AppSettingsEntity
                {
                    Id = _entries.Count + 1,
                    Key = key,
                    SettingsJson = value,
                    UpdatedAt = DateTime.UtcNow,
                };
                SaveCount++;
                return Task.FromResult(true);
            }
        }


        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_entries.Remove(key));


        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
