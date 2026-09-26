using Farm.Infrastructure.Services.HostUpdates;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.HostUpdate.Cli.Tests;

/// <summary>Remote or out-of-compose worker gate for offline activation and recovery (issue #3094).</summary>
public sealed class HostUpdateRemoteWorkerGuardTests
{
    [Theory]
    [InlineData("http://orcaslicer-worker:8080")]
    [InlineData("https://ORCASLICER-WORKER:8443/")]
    [InlineData("http://slicer-host:5246")]
    public void Compose_managed_worker_hosts_are_allowed(string host)
    {
        SlicerRegistrationRemoteWorkerGuard.Classify([host], new HostUpdateExecutionOptions()).Should().BeNull();
    }

    [Fact]
    public void No_registered_workers_is_allowed()
    {
        SlicerRegistrationRemoteWorkerGuard.Classify([], new HostUpdateExecutionOptions()).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("orcaslicer-worker")]
    [InlineData("http://worker-2.example.com:8080")]
    [InlineData("http://10.0.0.12:8080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://operator@orcaslicer-worker:8080")]
    [InlineData("ftp://orcaslicer-worker:8080")]
    [InlineData("http://printfarmer:8080")]
    public void Remote_or_unmanaged_worker_hosts_are_refused(string? host)
    {
        SlicerRegistrationRemoteWorkerGuard.Classify(["http://orcaslicer-worker:8080", host], new HostUpdateExecutionOptions())
            .Should().Be(SlicerRegistrationRemoteWorkerGuard.Unsupported);
    }

    [Fact]
    public void Worker_whose_compose_service_is_not_active_on_this_host_is_refused()
    {
        var options = new HostUpdateExecutionOptions { ActiveServiceIds = ["api", "frontend", "slicer-host", "printer-discovery"] };

        SlicerRegistrationRemoteWorkerGuard.Classify(["http://orcaslicer-worker:8080"], options)
            .Should().Be(SlicerRegistrationRemoteWorkerGuard.Unsupported);
    }

    [Fact]
    public async Task Guard_reads_registered_workers_from_the_slicer_database()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext database = CreateDatabase(connection);
        await database.Database.EnsureCreatedAsync();
        var guard = new SlicerRegistrationRemoteWorkerGuard(database, new HostUpdateExecutionOptions());

        (await guard.ValidateAsync(CancellationToken.None)).Should().BeNull();

        database.SlicerServices.Add(new SlicerService { Id = Guid.NewGuid(), Name = "local", Host = "http://orcaslicer-worker:8080" });
        await database.SaveChangesAsync();
        (await guard.ValidateAsync(CancellationToken.None)).Should().BeNull();

        database.SlicerServices.Add(new SlicerService { Id = Guid.NewGuid(), Name = "remote", Host = "http://worker-2.example.com:8080" });
        await database.SaveChangesAsync();
        (await guard.ValidateAsync(CancellationToken.None)).Should().Be(SlicerRegistrationRemoteWorkerGuard.Unsupported);
    }

    [Fact]
    public async Task Unreadable_worker_registrations_fail_closed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using SlicerDbContext database = CreateDatabase(connection);
        var guard = new SlicerRegistrationRemoteWorkerGuard(database, new HostUpdateExecutionOptions());

        (await guard.ValidateAsync(CancellationToken.None)).Should().Be(SlicerRegistrationRemoteWorkerGuard.EvidenceUnavailable);
    }

    private static SlicerDbContext CreateDatabase(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options);
}
