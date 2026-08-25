using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Exercises the production calibration generation route over HTTP: the authenticated generate-job
/// endpoint, the real repository and service graph, and the durable orchestration status document.
/// </summary>
/// <remarks>
/// Path D (#1980): <c>PrinterConfigurationSnapshot</c> was deleted, so
/// <see cref="ICalibrationGenerationSaga"/> now fails every orchestration terminally with
/// <see cref="CalibrationGenerationProblemCodes.ContextIdentityMissing"/> before a slice job is ever
/// submitted. The worker claim/download/upload/complete round trip and the resulting promotion into the
/// G-code library are unreachable until the filament-calibration saga (D7) replaces this context. This
/// class therefore only exercises what remains reachable over HTTP: acceptance of the request, the
/// durable terminal failure, and idempotent replay of that failure. The full happy-path saga mechanics
/// (lease handling, retry scheduling, event recording) are covered at the saga level by
/// <see cref="CalibrationGenerationSagaTests"/>.
/// </remarks>
public sealed class CalibrationGenerationIntegrationTests : IAsyncLifetime
{
    private static readonly Guid OwnerUserId = new("00000000-0000-0000-0000-000000000001");

    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
        });

    private CalibrationGenerationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await core.Database.EnsureCreatedAsync();
        }

        // The generate-job endpoint preflights an operational, allow-listed attested worker through
        // CalibrationCapabilityService before it will even accept a request; this is required for the
        // route to reach the saga at all, even though the worker never actually claims a job once the
        // saga fails terminally below.
        await RegisterAttestedWorkerAsync();

        _fixture = await CalibrationGenerationSeed.SeedAsync(
            CreateCoreContext,
            CalibrationMethodNames.Temperature,
            OwnerUserId,
            tamperSpecification: false);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact(DisplayName = "The production HTTP path accepts a generation request and durably fails when snapshot identity is unavailable")]
    public async Task GenerateJob_ThroughRealHttpRoute_FailsTerminallyWithContextIdentityMissing()
    {
        using HttpClient caller = CreateCallerClient();

        // 1. The authenticated caller starts the durable saga.
        HttpResponseMessage accepted = await PostGenerateJobAsync(caller, "integration-0001");
        string acceptedBody = await accepted.Content.ReadAsStringAsync();
        _ = accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, acceptedBody);
        _ = accepted.Headers.Location!.OriginalString.Should()
            .Be($"/api/calibration-orchestrations/{_fixture.OrchestrationId}");

        // 2. The durable saga can no longer resolve calibration context (the printer configuration
        // snapshot mechanism was removed), so it fails terminally instead of submitting a slice job.
        await AdvanceSagaAsync();

        CalibrationOrchestrationStatusDto status = await GetStatusAsync(caller);
        _ = status.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = status.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = status.GcodeFileId.Should().BeNull();

        await using AppDbContext core = CreateCoreContext();
        _ = (await core.CalibrationAttemptEvents
            .AsNoTracking()
            .Where(@event => @event.CalibrationOrchestrationId == _fixture.OrchestrationId)
            .Select(@event => @event.EventType)
            .ToListAsync()).Should().Contain("generation-failed");
    }

    [Fact(DisplayName = "Replaying the accepted request over HTTP after terminal failure returns the same durable status")]
    public async Task GenerateJob_ReplayedAfterTerminalFailure_ReturnsSameFailedStatusIdempotently()
    {
        using HttpClient caller = CreateCallerClient();
        _ = (await PostGenerateJobAsync(caller, "integration-replay")).StatusCode
            .Should().Be(HttpStatusCode.Accepted);
        await AdvanceSagaAsync();
        CalibrationOrchestrationStatusDto firstStatus = await GetStatusAsync(caller);

        // Replaying the same idempotency key must not resubmit or reprocess the already-failed run.
        HttpResponseMessage replay = await PostGenerateJobAsync(caller, "integration-replay");
        _ = replay.StatusCode.Should().Be(HttpStatusCode.OK);
        CalibrationOrchestrationStatusDto secondStatus = await GetStatusAsync(caller);

        _ = secondStatus.Status.Should().Be(firstStatus.Status);
        _ = secondStatus.Status.Should().Be(nameof(CalibrationOrchestrationStatus.Failed));
        _ = secondStatus.LastErrorCode.Should().Be(CalibrationGenerationProblemCodes.ContextIdentityMissing);
        _ = secondStatus.Revision.Should().Be(firstStatus.Revision);
    }

    private AppDbContext CreateCoreContext()
    {
        IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // The scope owns the context; disposing the context first keeps the caller's using pattern
        // honest while the scope is released with the factory.
        return core;
    }

    private HttpClient CreateCallerClient() => CreateCallerClient(OwnerUserId);

    private HttpClient CreateCallerClient(Guid ownerUserId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", ownerUserId.ToString());
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            string.Join(
                ',',
                PrintFarmerPermissions.Calibration.Generate,
                PrintFarmerPermissions.Calibration.Read,
                PrintFarmerPermissions.Slicing.Submit,
                PrintFarmerPermissions.Slicing.ReadArtifact,
                PrintFarmerPermissions.Queue.Read));
        return client;
    }

    private async Task<HttpResponseMessage> PostGenerateJobAsync(HttpClient client, string operationId)
        => await PostGenerateJobAsync(client, _fixture, operationId);

    private async Task<HttpResponseMessage> PostGenerateJobAsync(
        HttpClient client,
        CalibrationGenerationFixture fixture,
        string operationId)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"/api/calibration-projects/{fixture.ProjectId}/attempts/{fixture.AttemptId}/generate-job")
        {
            Content = JsonContent.Create(fixture.Request()),
        };
        message.Headers.Add("Idempotency-Key", operationId);
        return await client.SendAsync(message);
    }

    private async Task AdvanceSagaAsync() => await AdvanceSagaAsync(_fixture.OrchestrationId);

    private async Task AdvanceSagaAsync(Guid orchestrationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ICalibrationGenerationSaga saga = scope.ServiceProvider
            .GetRequiredService<ICalibrationGenerationSaga>();
        _ = await saga.ResumeAsync(orchestrationId, CancellationToken.None);
    }

    private async Task<CalibrationOrchestrationStatusDto> GetStatusAsync(HttpClient caller)
    {
        HttpResponseMessage response = await caller.GetAsync(
            $"/api/calibration-orchestrations/{_fixture.OrchestrationId}");
        string body = await response.Content.ReadAsStringAsync();
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonSerializer.Deserialize<CalibrationOrchestrationStatusDto>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task RegisterAttestedWorkerAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        SlicerDbContext slicer = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Guid serviceId = Guid.NewGuid();
        string apiKey = $"registry-issued-{Guid.NewGuid():N}";
        string capabilities = CalibrationGenerationSeed.BuildAttestationJson();

        _ = slicer.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "pinned-orca-service",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = CalibrationContractConstants.SlicerVersion,
            Host = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            MaxConcurrentJobs = 2,
            Status = WorkerStatus.Online,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = slicer.Workers.Add(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "pinned-orca-worker",
            EndpointUrl = "http://private-worker.internal",
            CapabilitiesJson = capabilities,
            Version = CalibrationContractConstants.SlicerVersion,
            ApiKey = apiKey,
            Status = WorkerStatus.Online,
            TotalSlots = 2,
            ActiveJobs = 0,
            LastHeartbeat = DateTime.UtcNow,
            RegisteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await slicer.SaveChangesAsync();
    }
}
