using Farm.Infrastructure.Data.Migrations;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class HostUpdateMigrationStepTests
{
    [Fact]
    public async Task MigrationTarget_FailsClosedForPendingMigrationProbeAndApply()
    {
        var target = new DbContextMigrationTarget<Microsoft.EntityFrameworkCore.DbContext>(
            "test",
            DatabaseMigrationTarget.Core,
            static () => throw new InvalidOperationException("Context resolution is not reached."),
            NullLogger<HostUpdateMigrationStepTests>.Instance);

        await Assert.ThrowsAsync<HostUpdateTargetImageMigrationRunnerUnavailableException>(
            () => target.HasPendingMigrationsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<HostUpdateTargetImageMigrationRunnerUnavailableException>(
            () => target.MigrateAsync(CancellationToken.None));
    }

}
