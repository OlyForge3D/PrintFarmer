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
        private readonly IOrcaDefaultProfileSeeder _seeder;
        private readonly ILogger<OrcaDefaultProfileSeedingHostedService> _logger;

        public OrcaDefaultProfileSeedingHostedService(IOrcaDefaultProfileSeeder seeder, ILogger<OrcaDefaultProfileSeedingHostedService> logger)
        {
            _seeder = seeder ?? throw new ArgumentNullException(nameof(seeder));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[OrcaSeederHosted] StartAsync invoked - attempting Orca default profile seeding.");
            try
            {
                Console.WriteLine("[Console][OrcaSeederHosted] StartAsync invoked - seeding attempt begins.");
                await _seeder.SeedAsync(cancellationToken);
                _logger.LogInformation("[OrcaSeederHosted] Seeding routine completed.");
                Console.WriteLine("[Console][OrcaSeederHosted] Seeding routine completed.");
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
