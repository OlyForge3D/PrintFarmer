using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.Slicing
{
    /// <summary>
    /// Hosted service wrapper to invoke the Orca default profile seeder during application startup.
    /// Ensures seeding occurs AFTER database initialization (Program initializes DB before StartAsync triggers hosted services).
    /// </summary>
    public class OrcaDefaultProfileSeedingHostedService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OrcaDefaultProfileSeedingHostedService> _logger;

        public OrcaDefaultProfileSeedingHostedService(IServiceScopeFactory scopeFactory, ILogger<OrcaDefaultProfileSeedingHostedService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[OrcaSeederHosted] StartAsync invoked - attempting Orca default profile seeding.");
            try
            {
                Console.WriteLine("[Console][OrcaSeederHosted] StartAsync invoked - seeding attempt begins.");

                // Create a scope to resolve scoped services (IOrcaDefaultProfileSeeder is registered as scoped)
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    var seeder = scope.ServiceProvider.GetService<IOrcaDefaultProfileSeeder>();
                    if (seeder == null)
                    {
                        _logger.LogWarning("[OrcaSeederHosted] IOrcaDefaultProfileSeeder not registered; skipping seeding.");
                        Console.WriteLine("[Console][OrcaSeederHosted] IOrcaDefaultProfileSeeder not registered; skipping seeding.");
                    }
                    else
                    {
                        await seeder.SeedAsync(cancellationToken);
                        _logger.LogInformation("[OrcaSeederHosted] Seeding routine completed.");
                        Console.WriteLine("[Console][OrcaSeederHosted] Seeding routine completed.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OrcaSeederHosted] Seeding routine failed.");
                Console.WriteLine("[Console][OrcaSeederHosted] Seeding routine failed: " + ex.Message);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Nothing to cleanup.
            return Task.CompletedTask;
        }
    }
}
