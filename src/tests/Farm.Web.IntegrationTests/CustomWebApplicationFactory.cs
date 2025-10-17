using System;
using Microsoft.AspNetCore.Mvc.Testing;

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
    }
}
