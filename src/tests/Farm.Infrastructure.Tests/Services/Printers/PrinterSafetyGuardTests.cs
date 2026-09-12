using Farm.Infrastructure.Services.Printers;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrinterSafetyGuardTests
{
    private static readonly DateTime Now =
        new(2026, 9, 9, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ValidateAsync_TargetHotButMeasuredCold_RejectsBelowMinimum()
    {
        Guid printerId = Guid.NewGuid();
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            CreateSafety(),
            CreateStatus(
                printerId,
                measured: 169,
                target: 250,
                observedAtUtc: Now));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.FilamentLoad,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("printer_temperature_below_minimum", result.Code);
    }

    [Fact]
    public async Task ValidateAsync_StaleMeasuredTemperature_RejectsStaleTelemetry()
    {
        Guid printerId = Guid.NewGuid();
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            CreateSafety(),
            CreateStatus(
                printerId,
                measured: 220,
                target: 220,
                observedAtUtc: Now.AddSeconds(-16)));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.FilamentUnload,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("printer_telemetry_stale", result.Code);
    }

    [Theory]
    [InlineData(VerifiedSafetySupport.Unsupported, 422, "printer_operation_unsupported")]
    [InlineData(VerifiedSafetySupport.Unknown, 503, "printer_safety_evidence_unknown")]
    public async Task ValidateAsync_UnavailableOperation_FailsClosed(
        VerifiedSafetySupport support,
        int statusCode,
        string code)
    {
        Guid printerId = Guid.NewGuid();
        PrinterVerifiedSafetyDto safety = CreateSafety() with
        {
            Operations = CreateSafety().Operations with
            {
                FilamentChange = new VerifiedSafetyOperationCapabilityDto(
                    support,
                    "test",
                    Now),
            },
        };
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            safety,
            CreateStatus(printerId, 220, 0, Now));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.FilamentChange,
            null,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task ValidateAsync_VerifiedMovementInsideBoundsAndClearance_Allows()
    {
        Guid printerId = Guid.NewGuid();
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            CreateSafety(),
            CreateStatus(printerId, 220, 0, Now));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.AbsoluteMovement,
            new PrinterSafetyMoveRequest(50, 60, 10),
            CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ValidateAsync_OriginOffset_UsesTheVerifiedEnvelopeCoordinateSpace()
    {
        Guid printerId = Guid.NewGuid();
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            CreateSafety(),
            CreateStatus(
                printerId,
                220,
                0,
                Now,
                new SafetyVector3Dto(10, 20, 30)));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.AbsoluteMovement,
            new PrinterSafetyMoveRequest(195, 190, 180),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal("printer_move_out_of_bounds", result.Code);
    }

    [Theory]
    [InlineData(250, 60, 10, "printer_move_out_of_bounds")]
    [InlineData(50, 60, 4, "printer_clearance_not_met")]
    public async Task ValidateAsync_InvalidMovement_RejectsGeometry(
        double x,
        double y,
        double z,
        string code)
    {
        Guid printerId = Guid.NewGuid();
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            CreateSafety(),
            CreateStatus(printerId, 220, 0, Now));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.AbsoluteMovement,
            new PrinterSafetyMoveRequest(x, y, z),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(code, result.Code);
    }

    [Fact]
    public async Task ValidateAsync_UnknownClearance_RejectsUnknownEvidence()
    {
        Guid printerId = Guid.NewGuid();
        PrinterVerifiedSafetyDto safety = CreateSafety() with
        {
            Positioning = CreateSafety().Positioning with
            {
                MinimumClearanceZMm = new VerifiedSafetyScalarFactDto(
                    VerifiedSafetyFactState.Unknown,
                    null,
                    "test",
                    Now),
            },
        };
        PrinterSafetyGuard guard = CreateGuard(
            printerId,
            safety,
            CreateStatus(printerId, 220, 0, Now));

        PrinterSafetyValidationResult result = await guard.ValidateAsync(
            printerId,
            PrinterSafetyOperation.AbsoluteMovement,
            new PrinterSafetyMoveRequest(50, 60, 10),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("printer_safety_evidence_unknown", result.Code);
    }

    [Fact]
    public async Task ValidateAsync_CancelledBeforeDiscovery_HonorsCancellation()
    {
        Guid printerId = Guid.NewGuid();
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>(
            MockBehavior.Strict);
        var guard = new PrinterSafetyGuard(
            capabilities.Object,
            Mock.Of<IPrinterStatusCacheReader>(),
            new FixedTimeProvider(Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            guard.ValidateAsync(
                printerId,
                PrinterSafetyOperation.FilamentLoad,
                null,
                cancellation.Token));
        capabilities.VerifyNoOtherCalls();
    }

    private static PrinterSafetyGuard CreateGuard(
        Guid printerId,
        PrinterVerifiedSafetyDto safety,
        PrinterStatusDto status)
    {
        var capabilities = new Mock<IPrinterBackendCapabilitiesService>();
        capabilities.Setup(service => service.InvalidateVerifiedSafety(printerId));
        capabilities.Setup(service => service.GetByPrinterIdAsync(
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterBackendCapabilitiesDto(
                printerId,
                "test",
                Farm.Infrastructure.Domain.PrinterBackend.Moonraker)
            {
                SupportsExtrusion = true,
                VerifiedSafety = safety,
            });
        var cache = new Mock<IPrinterStatusCacheReader>();
        cache.Setup(value => value.GetStatus(printerId)).Returns(status);
        return new PrinterSafetyGuard(
            capabilities.Object,
            cache.Object,
            new FixedTimeProvider(Now));
    }

    private static PrinterStatusDto CreateStatus(
        Guid printerId,
        double measured,
        double target,
        DateTime observedAtUtc,
        SafetyVector3Dto? coordinateOriginOffset = null) =>
        new(
            printerId,
            IsOnline: true,
            State: "Idle",
            SafetyTelemetry: new PrinterSafetyTelemetryDto(
                new SafetyScalarTelemetryFactDto(
                    measured,
                    observedAtUtc,
                    15,
                    "test.measured"),
                new SafetyScalarTelemetryFactDto(
                    target,
                    observedAtUtc,
                    15,
                    "test.target"),
                new SafetyAxesTelemetryFactDto(
                    ["x", "y", "z"],
                    observedAtUtc,
                    15,
                    "test.homed"),
                new SafetyVectorTelemetryFactDto(
                    coordinateOriginOffset ?? new SafetyVector3Dto(0, 0, 0),
                    observedAtUtc,
                    15,
                    "test.offset")));

    private static PrinterVerifiedSafetyDto CreateSafety()
    {
        var supported = new VerifiedSafetyOperationCapabilityDto(
            VerifiedSafetySupport.Supported,
            "test",
            Now);
        return new PrinterVerifiedSafetyDto(
            1,
            new VerifiedSafetyDiscoveryDto(
                VerifiedSafetyDiscoveryState.Verified,
                Now,
                "1"),
            new VerifiedSafetyOperationsDto(
                supported,
                supported,
                supported,
                supported,
                supported),
            new VerifiedSafetyExtrusionDto(
                new VerifiedSafetyScalarFactDto(
                    VerifiedSafetyFactState.Verified,
                    180,
                    "operator",
                    Now)),
            new VerifiedSafetyPositioningDto(
                new VerifiedSafetyVectorFactDto(
                    VerifiedSafetyFactState.Verified,
                    new SafetyVector3Dto(0, 0, 0),
                    "test",
                    Now),
                new VerifiedSafetyEnvelopeFactDto(
                    VerifiedSafetyFactState.Verified,
                    new SafetyTravelEnvelopeDto(
                        new SafetyVector3Dto(0, 0, 0),
                        new SafetyVector3Dto(200, 200, 200)),
                    "test",
                    Now),
                new VerifiedSafetyScalarFactDto(
                    VerifiedSafetyFactState.Verified,
                    5,
                    "operator",
                    Now)));
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(utcNow, TimeSpan.Zero);
    }
}
