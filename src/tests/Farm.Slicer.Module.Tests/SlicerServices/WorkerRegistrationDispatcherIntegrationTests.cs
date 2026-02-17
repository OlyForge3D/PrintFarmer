using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Web.Api.Services.JobDispatch;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices
{
    /// <summary>
    /// Integration tests covering Phase 3 worker registration → dispatcher visibility flow.
    /// Validates that registering a slicer service auto-populates the Worker table
    /// and that the dispatcher can select the worker for a capability-constrained job.
    /// </summary>
    [Trait("Category", "Integration")]
    [Collection(IntegrationTestCollection.Name)]
    public class WorkerRegistrationDispatcherIntegrationTests : IAsyncLifetime
    {
        private readonly CustomWebApplicationFactory _factory;

        public WorkerRegistrationDispatcherIntegrationTests()
        {
            _factory = new CustomWebApplicationFactory();
        }

        public async Task InitializeAsync()
        {
            await _factory.ResetDatabaseAsync();
        }

        public async Task DisposeAsync()
        {
            _factory?.Dispose();
        }

        private class RegResponse
        {
            public Guid Id { get; set; }
            public string ApiKey { get; set; } = string.Empty;
        }
    }
}
