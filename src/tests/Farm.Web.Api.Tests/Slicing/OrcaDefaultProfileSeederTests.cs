using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Slicing;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Verifies default Orca profile seeding runs (with force flag) and is idempotent.
/// </summary>
public class OrcaDefaultProfileSeederTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OrcaDefaultProfileSeederTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(DisplayName = "Default Orca profile seeding creates system profiles and is idempotent")]
    public async Task Seeder_CreatesProfiles_And_IsIdempotent()
    {
        // Force seeding in test environment (not Development)
        Environment.SetEnvironmentVariable("ORCA_PROFILE_SEED_FORCE", "true");

        // Trigger application startup (which invokes seeder in Program.cs)
        var client = _factory.CreateClient(); // client unused; host bootstraps here

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Initial count after startup seeding
            int initialCount = await db.SlicerProfiles
                .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer)
                .CountAsync();

            initialCount.Should().BeGreaterThanOrEqualTo(12, "expected seeded Fine/Standard/Draft profiles for X1 Carbon (PLA,PETG) and MK4 (PLA,PETG)");

            // Attempt second run (should no-op)
            var seeder = scope.ServiceProvider.GetRequiredService<IOrcaDefaultProfileSeeder>();
            await seeder.SeedAsync();

            int secondCount = await db.SlicerProfiles
                .Where(p => p.IsSystem && p.SlicerType == SlicerType.OrcaSlicer)
                .CountAsync();

            secondCount.Should().Be(initialCount, "seeding must be idempotent and not duplicate profiles");
        }
    }
}
