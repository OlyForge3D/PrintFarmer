using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data;
using Farm.Web.Api.Services.Startup;
using Farm.Web.Api.Startup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression tests for <see cref="MoonrakerEmulatorSeederDependenciesStartup"/> (#1858):
/// on a split/microservices API host, <see cref="MoonrakerEmulatorSeeder.ResetAsync"/> must
/// succeed against the exact DI registration <c>Program.cs</c> wires up at runtime, instead of
/// throwing when resolving <c>IMachineProfileRepository</c>/<c>IProcessProfileRepository</c>/
/// <c>IFilamentProfileRepository</c> (which previously turned
/// <c>POST /api/test/moonraker-emulator/reset</c> into an unconditional 500).
/// </summary>
public sealed class MoonrakerEmulatorSeederDependenciesStartupTests
{
    [Fact]
    public void AddMoonrakerEmulatorSeederDependencies_MonolithDeployment_IsNoOp()
    {
        // No DEPLOYMENT_MODE configured => monolith. Monolith hosts get these repositories from
        // AddSlicerModule instead, so this method must not add anything on its own.
        IConfiguration configuration = new ConfigurationBuilder().Build();

        ServiceCollection services = new();
        int countBefore = services.Count;

        _ = services.AddMoonrakerEmulatorSeederDependencies(configuration);

        Assert.Equal(countBefore, services.Count);
    }

    [Fact]
    public async Task ResetAsync_SplitDeploymentDependencyRegistration_SucceedsAgainstRealRepositories()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"moonraker-seeder-split-{Guid.NewGuid():N}.db");
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEPLOYMENT_MODE"] = "microservices",
                    ["DB_PROVIDER"] = "sqlite",
                    ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
                })
                .Build();

            var printers = new Mock<IPrintersRepository>(MockBehavior.Strict);
            printers
                .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            List<Printer> added = [];
            printers
                .Setup(repository => repository.AddAsync(
                    It.IsAny<Printer>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Printer, CancellationToken>((printer, _) => added.Add(printer))
                .Returns(Task.CompletedTask);

            var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
            unitOfWork.SetupGet(value => value.Printers).Returns(printers.Object);

            var queue = new Mock<IQueueRepository>(MockBehavior.Strict);
            queue
                .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            List<PrintJob> jobs = [];
            queue
                .Setup(repository => repository.AddWithoutSaveAsync(
                    It.IsAny<PrintJob>(),
                    It.IsAny<CancellationToken>()))
                .Callback<PrintJob, CancellationToken>((job, _) => jobs.Add(job))
                .Returns(Task.CompletedTask);
            unitOfWork.SetupGet(value => value.Queue).Returns(queue.Object);
            unitOfWork
                .Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => added.Count);
            unitOfWork.Setup(value => value.Dispose());

            Guid manufacturerId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            var catalog = new Mock<ICatalogRepository>(MockBehavior.Strict);
            catalog
                .Setup(repository => repository.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            catalog
                .Setup(repository => repository.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            catalog
                .Setup(repository => repository.FindManufacturerByNameAsync(
                    "Voron",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Manufacturer { Id = manufacturerId, Name = "Voron" });
            catalog
                .Setup(repository => repository.FindModelByNameAsync(
                    "Voron 2.4 300",
                    manufacturerId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PrinterModel
                {
                    Id = modelId,
                    Name = "Voron 2.4 300",
                    ManufacturerId = manufacturerId,
                });

            var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
            printersService
                .Setup(service => service.RefreshCameraUrlsAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((PrinterDto?)null);

            ServiceCollection services = new();
            _ = services.AddSingleton(unitOfWork.Object);
            _ = services.AddSingleton(catalog.Object);
            _ = services.AddSingleton(printersService.Object);

            // The exact wiring under test: Program.cs calls this on every split/microservices
            // API host so MoonrakerEmulatorSeeder can resolve its calibration profile
            // repositories from its own DI scope (#1858).
            _ = services.AddMoonrakerEmulatorSeederDependencies(configuration);

            await using ServiceProvider provider = services.BuildServiceProvider();
            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();
            }

            var seeder = new MoonrakerEmulatorSeeder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new MoonrakerEmulatorSeedSettings { Enabled = true }),
                NullLogger<MoonrakerEmulatorSeeder>.Instance);

            // Before the fix, this threw resolving IMachineProfileRepository/
            // IProcessProfileRepository/IFilamentProfileRepository, which the controller let
            // propagate as an unhandled exception (HTTP 500). It must now return true (204).
            bool result = await seeder.ResetAsync(CancellationToken.None);

            Assert.True(result);
            Assert.Equal(5, added.Count);

            await using AsyncServiceScope assertScope = provider.CreateAsyncScope();
            SlicerDbContext assertDb = assertScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            Assert.Equal(1, await assertDb.MachineProfiles.CountAsync());
            Assert.Equal(1, await assertDb.ProcessProfiles.CountAsync());
            Assert.Equal(1, await assertDb.FilamentProfiles.CountAsync());
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections by default, which keeps the file locked
            // for a short while after disposal — clear the pool before deleting.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(dbPath);
        }
    }
}
