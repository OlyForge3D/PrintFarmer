using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Services.Spoolman;

public sealed class SpoolBurnRateProjectionServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private readonly Mock<IFilamentCoverageSpoolResolver> _resolver = new();
    private readonly Mock<ISettingsService> _settings = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using AppDbContext db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        _settings.Setup(service => service.Get<ShiftPlanSettings>())
            .Returns(new ShiftPlanSettings
            {
                SpoolBurnRateLookbackDays = 30,
                SpoolBurnRateMinimumSamples = 3,
                SpoolReorderThresholdGrams = 250,
            });
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ProjectAsync_SourceCollision_ProducesIndependentHistories()
    {
        CanonicalSpoolIdentity central = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        CanonicalSpoolIdentity nativeA = Identity(
            SpoolSourceKind.MoonrakerNative,
            "http://moon-a.local");
        CanonicalSpoolIdentity nativeB = Identity(
            SpoolSourceKind.MoonrakerNative,
            "http://moon-b.local");
        await SeedCompletedSampleAsync(central, 30, Now.AddDays(-1));
        await SeedCompletedSampleAsync(nativeA, 60, Now.AddDays(-1));
        await SeedCompletedSampleAsync(nativeB, 90, Now.AddDays(-1));
        SetupRemaining(central, 100);
        SetupRemaining(nativeA, 200);
        SetupRemaining(nativeB, 300);
        SetMinimumSamples(1);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionService service = CreateService(projectionContext);

        SpoolBurnRateProjectionDto centralResult =
            await service.ProjectAsync(central);
        SpoolBurnRateProjectionDto nativeAResult =
            await service.ProjectAsync(nativeA);
        SpoolBurnRateProjectionDto nativeBResult =
            await service.ProjectAsync(nativeB);

        centralResult.AuthoritativeGramsConsumed.Should().Be(30);
        nativeAResult.AuthoritativeGramsConsumed.Should().Be(60);
        nativeBResult.AuthoritativeGramsConsumed.Should().Be(90);
        centralResult.SampleCount.Should().Be(1);
        nativeAResult.SampleCount.Should().Be(1);
        nativeBResult.SampleCount.Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_PathCaseDifference_ProducesIndependentHistories()
    {
        CanonicalSpoolIdentity upperPath = Identity(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local/App");
        CanonicalSpoolIdentity lowerPath = Identity(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local/app");
        await SeedCompletedSampleAsync(upperPath, 30, Now.AddDays(-1));
        await SeedCompletedSampleAsync(lowerPath, 90, Now.AddDays(-1));
        SetupRemaining(upperPath, 100);
        SetupRemaining(lowerPath, 200);
        SetMinimumSamples(1);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionService service = CreateService(projectionContext);

        SpoolBurnRateProjectionDto upperResult =
            await service.ProjectAsync(upperPath);
        SpoolBurnRateProjectionDto lowerResult =
            await service.ProjectAsync(lowerPath);

        upperResult.AuthoritativeGramsConsumed.Should().Be(30);
        lowerResult.AuthoritativeGramsConsumed.Should().Be(90);
        upperResult.SampleCount.Should().Be(1);
        lowerResult.SampleCount.Should().Be(1);
    }

    [Fact]
    public async Task ProjectAsync_ExactBoundaries_ComputesRateAndThresholdCrossing()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 30, Now.AddDays(-30));
        await SeedCompletedSampleAsync(identity, 20, Now.AddDays(-10));
        await SeedCompletedSampleAsync(identity, 40, Now);
        await SeedCompletedSampleAsync(identity, 999, Now.AddDays(-30).AddTicks(-1));
        SetupRemaining(identity, 340);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.Ready);
        result.SampleCount.Should().Be(3);
        result.AuthoritativeGramsConsumed.Should().Be(90);
        result.BurnRateGramsPerDay.Should().Be(3);
        result.ProjectedThresholdCrossingUtc.Should().Be(Now.AddDays(30).UtcDateTime);
        result.EvaluatedAtUtc.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task ProjectAsync_IneligibleHistory_IsExcluded()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 10, Now.AddDays(-1));
        await SeedSampleAsync(identity, 20, Now.AddDays(-1), PrintJobStatus.Failed);
        await SeedSampleAsync(identity, 30, Now.AddDays(-1), PrintJobStatus.Cancelled);
        await SeedUnqualifiedSampleAsync(40, Now.AddDays(-1));
        await SeedEstimatedSampleAsync(identity, 50, Now.AddDays(-1));
        SetupRemaining(identity, 500);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.InsufficientData);
        result.SampleCount.Should().Be(1);
        result.AuthoritativeGramsConsumed.Should().Be(10);
        result.BurnRateGramsPerDay.Should().BeNull();
        result.ProjectedThresholdCrossingUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_DuplicateAttributionRetry_ContributesOnce()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await using (AppDbContext writeContext = CreateContext())
        {
            PrintJob job = CompletedJob(Now.AddDays(-1));
            PrintJobToolheadUsage usage = Usage(job, identity);
            usage.RecordAuthoritativeUsage(12, identity).Should().BeTrue();
            usage.RecordAuthoritativeUsage(99, identity).Should().BeFalse();
            writeContext.AddRange(job, usage);
            await writeContext.SaveChangesAsync();
        }

        SetupRemaining(identity, 500);
        SetMinimumSamples(1);
        await using AppDbContext projectionContext = CreateContext();

        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.SampleCount.Should().Be(1);
        result.AuthoritativeGramsConsumed.Should().Be(12);
    }

    [Fact]
    public async Task ProjectAsync_SourceUnavailable_ReturnsNonReadyWithoutCrossing()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 90, Now.AddDays(-1));
        SetMinimumSamples(1);
        _resolver.Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                null,
                false,
                FilamentCoverageSpoolResolver.ReasonSourceUnavailable));

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.SourceUnavailable);
        result.RemainingGrams.Should().BeNull();
        result.ProjectedThresholdCrossingUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_MissingRemainingWeight_ReturnsNonReadyWithoutCrossing()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 90, Now.AddDays(-1));
        SetMinimumSamples(1);
        _resolver.Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                new SpoolmanSpoolDto(42, "Spool 42", "PLA", null, null, true),
                false,
                null));

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.SourceUnavailable);
        result.BurnRateGramsPerDay.Should().Be(3);
        result.ProjectedThresholdCrossingUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_ThresholdAlreadyCrossed_UsesEvaluationTime()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 30, Now.AddDays(-1));
        SetupRemaining(identity, 200);
        SetMinimumSamples(1);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.Ready);
        result.ProjectedThresholdCrossingUtc.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task ProjectAsync_UnrepresentableCrossing_ReturnsNonReadyWithoutThrowing()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 1e-300, Now.AddDays(-1));
        SetupRemaining(identity, double.MaxValue);
        SetMinimumSamples(1);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.InsufficientData);
        result.ProjectedThresholdCrossingUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_NonFiniteRemainingWeight_ReturnsSourceUnavailable()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, 30, Now.AddDays(-1));
        SetupRemaining(identity, double.PositiveInfinity);
        SetMinimumSamples(1);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.SourceUnavailable);
        result.RemainingGrams.Should().BeNull();
        result.ProjectedThresholdCrossingUtc.Should().BeNull();
    }

    [Fact]
    public async Task ProjectAsync_OverflowingConsumption_ReturnsSerializableNonReadyResult()
    {
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local");
        await SeedCompletedSampleAsync(identity, double.MaxValue, Now.AddDays(-1));
        await SeedCompletedSampleAsync(identity, double.MaxValue, Now.AddDays(-2));
        SetupRemaining(identity, 1000);
        SetMinimumSamples(2);

        await using AppDbContext projectionContext = CreateContext();
        SpoolBurnRateProjectionDto result =
            await CreateService(projectionContext).ProjectAsync(identity);

        result.State.Should().Be(SpoolBurnRateProjectionState.InsufficientData);
        result.AuthoritativeGramsConsumed.Should().Be(double.MaxValue);
        result.BurnRateGramsPerDay.Should().BeNull();

        ServiceCollection services = new();
        _ = services.AddLogging();
        _ = services.AddPrintFarmerControllers();
        await using ServiceProvider provider = services.BuildServiceProvider();
        JsonOptions jsonOptions =
            provider.GetRequiredService<IOptions<JsonOptions>>().Value;

        string json = JsonSerializer.Serialize(
            result,
            jsonOptions.JsonSerializerOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        double serializedConsumption = document.RootElement
            .GetProperty("authoritativeGramsConsumed")
            .GetDouble();
        double.IsFinite(serializedConsumption).Should().BeTrue();
    }

    private AppDbContext CreateContext() => new(_options);

    private SpoolBurnRateProjectionService CreateService(AppDbContext db)
        => new(
            db,
            _resolver.Object,
            _settings.Object,
            new FixedTimeProvider(Now));

    private static CanonicalSpoolIdentity Identity(
        SpoolSourceKind sourceKind,
        string sourceIdentity)
        => new(sourceKind, sourceIdentity, 42);

    private void SetMinimumSamples(int minimumSamples)
    {
        _settings.Setup(service => service.Get<ShiftPlanSettings>())
            .Returns(new ShiftPlanSettings
            {
                SpoolBurnRateLookbackDays = 30,
                SpoolBurnRateMinimumSamples = minimumSamples,
                SpoolReorderThresholdGrams = 250,
            });
    }

    private void SetupRemaining(
        CanonicalSpoolIdentity identity,
        double remainingGrams)
    {
        _resolver.Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                new SpoolmanSpoolDto(
                    identity.SpoolId,
                    $"Spool {identity.SpoolId}",
                    "PLA",
                    remainingGrams,
                    null,
                    true),
                identity.SourceKind == SpoolSourceKind.MoonrakerNative,
                null));
    }

    private Task SeedCompletedSampleAsync(
        CanonicalSpoolIdentity identity,
        double grams,
        DateTimeOffset completedAt)
        => SeedSampleAsync(
            identity,
            grams,
            completedAt,
            PrintJobStatus.Completed);

    private async Task SeedSampleAsync(
        CanonicalSpoolIdentity identity,
        double grams,
        DateTimeOffset completedAt,
        PrintJobStatus status)
    {
        await using AppDbContext db = CreateContext();
        PrintJob job = CompletedJob(completedAt, status);
        PrintJobToolheadUsage usage = Usage(job, identity);
        _ = usage.RecordAuthoritativeUsage(grams, identity);
        db.AddRange(job, usage);
        await db.SaveChangesAsync();
    }

    private async Task SeedUnqualifiedSampleAsync(
        double grams,
        DateTimeOffset completedAt)
    {
        await using AppDbContext db = CreateContext();
        PrintJob job = CompletedJob(completedAt);
        PrintJobToolheadUsage usage = new()
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrintJob = job,
            ToolheadIndex = 0,
            SpoolmanSpoolId = 42,
        };
        _ = usage.RecordAuthoritativeUsage(grams, null);
        db.AddRange(job, usage);
        await db.SaveChangesAsync();
    }

    private async Task SeedEstimatedSampleAsync(
        CanonicalSpoolIdentity identity,
        double grams,
        DateTimeOffset completedAt)
    {
        await using AppDbContext db = CreateContext();
        PrintJob job = CompletedJob(completedAt);
        PrintJobToolheadUsage usage = Usage(job, identity);
        usage.RecordEstimatedUsage(grams);
        db.AddRange(job, usage);
        await db.SaveChangesAsync();
    }

    private static PrintJob CompletedJob(
        DateTimeOffset completedAt,
        PrintJobStatus status = PrintJobStatus.Completed)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "projection-test.gcode",
            Status = status,
            ActualEndTime = completedAt.UtcDateTime,
            CreatedAt = completedAt.AddHours(-1).UtcDateTime,
            UpdatedAt = completedAt.UtcDateTime,
            QueuedAt = completedAt.AddHours(-1).UtcDateTime,
        };

    private static PrintJobToolheadUsage Usage(
        PrintJob job,
        CanonicalSpoolIdentity identity)
        => new()
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrintJob = job,
            ToolheadIndex = 0,
            SpoolmanSpoolId = identity.SpoolId,
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
