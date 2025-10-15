using System;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;

namespace Farm.Web.Api.Tests
{
    // Minimal compatibility CustomWebApplicationFactory providing the helper
    // methods many tests rely on. These are intentionally simple/no-op
    // implementations so the repo can build after the cleanup pass.
    public class CustomWebApplicationFactory : WebApplicationFactory<Program>
    {
        public CustomWebApplicationFactory()
        {
        }

        public static CustomWebApplicationFactory CreateWithIsolatedDatabase(bool useInMemorySqlite = true)
        {
            // Tests expect a factory instance configured for an isolated DB.
            // For now return a plain factory; tests that need isolation should
            // set environment variables themselves or use TestHelpers.
            return new CustomWebApplicationFactory();
        }

        // Generic mock helpers: use generic type parameter so callers with
        // Action<Mock<T>> lambdas will type-infer T correctly.
        public CustomWebApplicationFactory MockNetworkDiscoveryService<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockMoonrakerClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockPrusaLinkClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSdcpClient<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerJobQueue<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerFileStorage<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockSlicerProgressNotifier<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }

        public CustomWebApplicationFactory MockModelAnalysisService<T>(Action<Mock<T>>? setup = null)
            where T : class
        {
            setup?.Invoke(new Mock<T>());
            return this;
        }
    }
}
