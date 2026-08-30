using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Testing.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Wire-contract corpus for the slice-job family (<c>GET /api/slice/{id}</c>). Issue #2238:
/// fixtures are produced by a real <c>WebApplicationFactory</c> HTTP round trip through the
/// actual registered MVC <c>JsonSerializerOptions</c> (<c>src/api/Startup/ControllerStartup.cs</c>),
/// against a job seeded through the real production <see cref="ISliceJobRepository"/> (never a
/// hand-built CLR object serialized independently).
/// </summary>
public sealed class SliceJobContractTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    /// <summary>
    /// Minimal + missing-key variant: a just-submitted job has no worker assignment, no
    /// progress, no completion, and no failure — so every optional field is missing from the
    /// wire payload, while <c>status</c>/<c>slicerEngine</c> are present as their exact real
    /// production string tokens.
    /// </summary>
    [Fact]
    public async Task GetJob_JustSubmitted_MinimalStatusOmitsOptionalKeys()
    {
        Guid jobId = await SeedJobAsync(job =>
        {
            job.Status = SliceJobStatus.Queued;
        });

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-slicejob-minimal",
            email: "wire-contract-slicejob-minimal@example.com");

        using HttpResponseMessage response = await client.GetAsync($"/api/slice/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonContractAssertions.AssertEnumToken(root, "status", "Queued");
        JsonContractAssertions.AssertEnumToken(root, "slicerEngine", "OrcaSlicer");
        _ = JsonContractAssertions.AssertProperty(root, "progressPercent", JsonValueKind.Number);

        JsonContractAssertions.AssertMissingKey(root, "progressMessage");
        JsonContractAssertions.AssertMissingKey(root, "startedAt");
        JsonContractAssertions.AssertMissingKey(root, "completedAt");
        JsonContractAssertions.AssertMissingKey(root, "errorMessage");
        JsonContractAssertions.AssertMissingKey(root, "errorDetail");
        JsonContractAssertions.AssertMissingKey(root, "layoutDegradation");
        JsonContractAssertions.AssertMissingKey(root, "failureReason");
        JsonContractAssertions.AssertMissingKey(root, "failureHint");
        JsonContractAssertions.AssertMissingKey(root, "estimatedPrintTimeSeconds");
        JsonContractAssertions.AssertMissingKey(root, "filamentUsedGrams");
        JsonContractAssertions.AssertMissingKey(root, "workerId");

        var volatilePaths = new HashSet<string> { "$.id", "$.queuedAt", "$.artifactsRoute" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slice-jobs/job.minimal-status.json",
            endpoint: "GET /api/slice/{id}",
            producingTest: $"{nameof(SliceJobContractTests)}.{nameof(GetJob_JustSubmitted_MinimalStatusOmitsOptionalKeys)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    /// <summary>
    /// Populated variant: a completed job with a worker assignment, print-time/filament
    /// estimates and a result. Every real-production nullable field this repository method
    /// populates is present.
    /// </summary>
    [Fact]
    public async Task GetJob_Completed_PopulatedFieldsMatchCorpus()
    {
        Guid jobId = await SeedJobAsync(job =>
        {
            job.Status = SliceJobStatus.Processing;
            job.ProgressPercent = 100;
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        ISliceJobRepository repository = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();
        await repository.MarkCompletedAsync(
            jobId,
            resultFileUrl: "/api/artifacts/wire-contract-result.3mf",
            estimatedPrintTimeSeconds: 5400,
            filamentUsedGrams: 42.75m);

        using HttpClient client = await _factory.CreateAdminClientAsync(
            username: "wire-contract-slicejob-populated",
            email: "wire-contract-slicejob-populated@example.com");

        using HttpResponseMessage response = await client.GetAsync($"/api/slice/{jobId}");
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonContractAssertions.AssertEnumToken(root, "status", "Completed");
        _ = JsonContractAssertions.AssertProperty(root, "completedAt", JsonValueKind.String);
        _ = JsonContractAssertions.AssertProperty(root, "estimatedPrintTimeSeconds", JsonValueKind.Number);
        _ = JsonContractAssertions.AssertProperty(root, "filamentUsedGrams", JsonValueKind.Number);
        JsonContractAssertions.AssertMissingKey(root, "errorMessage");
        JsonContractAssertions.AssertMissingKey(root, "failureReason");

        var volatilePaths = new HashSet<string> { "$.id", "$.queuedAt", "$.startedAt", "$.completedAt", "$.artifactsRoute" };
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            WireContractCorpusPaths.ApiRoot,
            "slice-jobs/job.completed-populated.json",
            endpoint: "GET /api/slice/{id}",
            producingTest: $"{nameof(SliceJobContractTests)}.{nameof(GetJob_Completed_PopulatedFieldsMatchCorpus)}",
            schemaVersion: "1.0",
            actualJson: json,
            volatilePaths: volatilePaths);
    }

    private async Task<Guid> SeedJobAsync(Action<SliceJob> configure)
    {
        Guid jobId = Guid.NewGuid();
        using IServiceScope scope = _factory.Services.CreateScope();
        ISliceJobRepository repository = scope.ServiceProvider.GetRequiredService<ISliceJobRepository>();

        var job = new SliceJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            ModelFileUrl = "/api/3d-models/wire-contract-model.3mf",
            ModelFileName = "wire-contract-model.3mf",
            SlicerEngineName = "OrcaSlicer",
            QueuedAt = DateTime.UtcNow,
        };
        configure(job);

        await repository.AddAsync(job);
        return jobId;
    }
}
