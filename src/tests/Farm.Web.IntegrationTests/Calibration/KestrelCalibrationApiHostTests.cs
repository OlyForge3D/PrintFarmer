using System.Data.Common;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Migrations;
using Farm.Slicer.Module.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.IntegrationTests.Calibration;

public sealed class KestrelCalibrationApiHostTests
{
    [Fact]
    public async Task Start_IsolatedFixtures_UsesOneHostAndCompleteIndependentSchemas()
    {
        string workspace = Path.Join(Path.GetTempPath(), $"pfarm-kestrel-regression-{Guid.NewGuid():N}");
        int buildBoundaryChecks = 0;
        try
        {
            await using KestrelCalibrationApiHost first = KestrelCalibrationApiHost.Start(
                Path.Join(workspace, "first"), VerifySchemaBeforeBuild);
            await using KestrelCalibrationApiHost second = KestrelCalibrationApiHost.Start(
                Path.Join(workspace, "second"), VerifySchemaBeforeBuild);
            Assert.Equal(2, buildBoundaryChecks);
            Assert.Same(first.Services, first.ListeningServices);
            Assert.Same(second.Services, second.ListeningServices);
            Assert.IsNotType<Microsoft.AspNetCore.TestHost.TestServer>(
                first.ListeningServices.GetRequiredService<IServer>());

            using IServiceScope firstScope = first.ListeningServices.CreateScope();
            using IServiceScope secondScope = second.ListeningServices.CreateScope();
            AppDbContext firstCore = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            AppDbContext secondCore = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotEqual(firstCore.Database.GetConnectionString(), secondCore.Database.GetConnectionString());
            foreach (IServiceScope scope in new[] { firstScope, secondScope })
            {
                Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                    .Database.GetPendingMigrationsAsync());
                Assert.Empty(await scope.ServiceProvider.GetRequiredService<SlicerDbContext>()
                    .Database.GetPendingMigrationsAsync());
            }

            await first.WaitUntilHealthyAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await second.WaitUntilHealthyAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        void VerifySchemaBeforeBuild(SqliteConnection connection)
        {
            using AppDbContext core = new(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
                .Options);
            using SlicerDbContext slicer = new(new DbContextOptionsBuilder<SlicerDbContext>()
                .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"))
                .Options);
            Assert.Empty(core.Database.GetPendingMigrations());
            Assert.Empty(slicer.Database.GetPendingMigrations());
            buildBoundaryChecks++;
        }
    }

    [Fact]
    public async Task MigrateAsync_FixtureMigratesAfterStartupHistoryRead_RejectsIntermediateSchema()
    {
        await using SqliteConnection connection = new(
            $"Data Source=file:migration-race-{Guid.NewGuid():N}?mode=memory&cache=shared;Pooling=False");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> fixtureOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .Options;
        using AppDbContext fixtureContext = new(fixtureOptions);
        var interceptor = new InitialMigrationAfterHistoryRead(fixtureContext);
        DbContextOptions<AppDbContext> startupOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection, options => options.MigrationsAssembly("Farm.Migrations.Sqlite"))
            .AddInterceptors(interceptor)
            .Options;
        using AppDbContext startupContext = new(startupOptions);

        DatabaseMigrationContractException exception = await Assert.ThrowsAsync<DatabaseMigrationContractException>(
            () => ProviderAwareMigrationRunner.MigrateAsync(
                startupContext, DatabaseMigrationTarget.Core, NullLogger.Instance));
        Assert.True(interceptor.Applied);
        Assert.Equal("schema_validation_failed", exception.Code);
        Assert.Contains("CalibrationAttempts.PrinterConfigurationSnapshotId (unexpected column)", exception.Message);
        Assert.Contains("No legacy baseline was recorded.", exception.Message);
    }

    private sealed class InitialMigrationAfterHistoryRead(AppDbContext fixtureContext) : DbCommandInterceptor
    {
        public bool Applied { get; private set; }

        public override async ValueTask<object?> ScalarExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            object? result,
            CancellationToken cancellationToken = default)
        {
            if (!Applied && command.CommandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            {
                Assert.Equal(0L, Assert.IsType<long>(result));
                Applied = true;
                // Freeze the interleaving after startup sees no history, but before it checks Printers.
                // InitialV2 creates obsolete columns that later fixture migrations remove.
                await fixtureContext.GetService<IMigrator>().MigrateAsync(
                    fixtureContext.Database.GetMigrations().First(), cancellationToken);
            }

            return result;
        }
    }
}
