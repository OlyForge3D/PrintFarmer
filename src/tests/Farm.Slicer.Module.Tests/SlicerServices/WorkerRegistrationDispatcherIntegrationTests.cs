using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Integration tests covering Phase 3 worker registration → dispatcher visibility flow.
/// Validates that registering a slicer service auto-populates the Worker table
/// and that the dispatcher can select the worker for a capability-constrained job.
/// </summary>
[Trait("Category", "Integration")]
public class WorkerRegistrationDispatcherIntegrationTests : IAsyncLifetime, IDisposable
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

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
