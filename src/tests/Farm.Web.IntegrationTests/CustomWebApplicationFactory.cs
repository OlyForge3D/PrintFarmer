using System;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.IntegrationTests
{
    // Minimal compatibility CustomWebApplicationFactory for integration tests
    // Provides a simple WebApplicationFactory<Program> so tests can run in this environment.
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public CustomWebApplicationFactory()
        {
        }

        public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
        {
            return new CustomWebApplicationFactory();
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Provide a lightweight test orchestrator so integration tests can run without full worker infrastructure.
                services.AddSingleton<Farm.Web.Shared.ISlicerOrchestrator, TestSlicerOrchestrator>();
            });
        }
    }
}
