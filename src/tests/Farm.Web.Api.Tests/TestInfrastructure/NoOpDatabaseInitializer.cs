using System.Threading.Tasks;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Web.Api.Services.Startup;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Tests.TestInfrastructure
{
    // Lightweight no-op implementation used in tests that use the EF InMemory provider
    // to avoid executing heavy seeding logic which may depend on SQL-specific behaviors.
    public sealed class NoOpDatabaseInitializer(Farm.Infrastructure.Data.AppDbContext context, ILogger<DatabaseInitializer> logger, IDataSeedService dataSeedService) : DatabaseInitializer(context, logger, dataSeedService)
    {
        public override Task InitializeAsync(string dbProvider, int maxRetries = 10, int delaySeconds = 5)
        {
            // Intentionally no-op for tests using InMemory provider
            return Task.CompletedTask;
        }

        public override Task SeedAllAsync()
        {
            return Task.CompletedTask;
        }
    }
}
