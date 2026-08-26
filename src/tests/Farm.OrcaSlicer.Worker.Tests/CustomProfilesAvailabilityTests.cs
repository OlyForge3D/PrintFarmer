using System.Net;
using Farm.OrcaSlicer.Worker.Health;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class CustomProfilesAvailabilityTests
{
    [Fact]
    public async Task ClaimHandler_ReconciliationPending_RejectsOnlyClaim()
    {
        CustomProfilesReconciliationState state = new();
        using var handler = new CustomProfilesClaimAvailabilityHandler(
            state,
            () => "unused")
        {
            InnerHandler = new SuccessHandler(),
        };
        using HttpMessageInvoker client = new(handler);
        using HttpRequestMessage claim =
            new(HttpMethod.Post, "http://api/api/slice/claim");
        using HttpRequestMessage complete =
            new(HttpMethod.Post, "http://api/api/slice/jobs/id/complete");

        using HttpResponseMessage claimResponse =
            await client.SendAsync(claim, CancellationToken.None);
        using HttpResponseMessage completeResponse =
            await client.SendAsync(complete, CancellationToken.None);

        claimResponse.StatusCode.Should()
            .Be(HttpStatusCode.ServiceUnavailable);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClaimHandler_SharedFingerprintChanged_BlocksUntilLoaded()
    {
        CustomProfilesReconciliationState state = new();
        state.MarkReady("loaded");
        string sharedFingerprint = "changed";
        using var handler = new CustomProfilesClaimAvailabilityHandler(
            state,
            () => sharedFingerprint)
        {
            InnerHandler = new SuccessHandler(),
        };
        using HttpMessageInvoker client = new(handler);
        using HttpRequestMessage staleClaim =
            new(HttpMethod.Post, "http://api/api/slice/claim");

        using HttpResponseMessage staleResponse =
            await client.SendAsync(staleClaim, CancellationToken.None);

        staleResponse.StatusCode.Should()
            .Be(HttpStatusCode.ServiceUnavailable);
        state.IsReady.Should().BeFalse();

        state.MarkReady(sharedFingerprint);
        using HttpRequestMessage currentClaim =
            new(HttpMethod.Post, "http://api/api/slice/claim");
        using HttpResponseMessage currentResponse =
            await client.SendAsync(currentClaim, CancellationToken.None);

        currentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task ProfilesAreCurrent_WhenFingerprintThrows_FailsClosed(
        Type exceptionType)
    {
        CustomProfilesReconciliationState state = new();
        state.MarkReady("loaded");
        int fingerprintCalls = 0;
        SuccessHandler innerHandler = new();
        using var handler = new CustomProfilesClaimAvailabilityHandler(
            state,
            () =>
            {
                fingerprintCalls++;
                throw exceptionType == typeof(IOException)
                    ? new IOException()
                    : new InvalidOperationException();
            })
        {
            InnerHandler = innerHandler,
        };
        using HttpMessageInvoker client = new(handler);
        using HttpRequestMessage claim =
            new(HttpMethod.Post, "http://api/api/slice/claim");
        using HttpRequestMessage complete =
            new(HttpMethod.Post, "http://api/api/slice/jobs/id/complete");

        using HttpResponseMessage claimResponse =
            await client.SendAsync(claim, CancellationToken.None);
        using HttpResponseMessage completeResponse =
            await client.SendAsync(complete, CancellationToken.None);

        claimResponse.StatusCode.Should()
            .Be(HttpStatusCode.ServiceUnavailable);
        claimResponse.ReasonPhrase.Should()
            .Be("Custom profiles are not synchronized");
        state.IsReady.Should().BeFalse();
        state.Failure.Should()
            .Be("Shared custom profiles changed; local reconciliation is pending.");
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        fingerprintCalls.Should().Be(1);
        innerHandler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ReconciliationHealthCheck_StateChanges_TracksReadiness()
    {
        CustomProfilesReconciliationState state = new();
        state.MarkUnavailable("shared profile volume is inconsistent");
        CustomProfilesReconciliationHealthCheck healthCheck = new(state);

        HealthCheckResult result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should()
            .Be("shared profile volume is inconsistent");

        state.MarkReady("loaded");
        result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void HeartbeatAvailability_CustomProfilesUnavailable_IsErrorWithNoSlots()
    {
        WorkerState workerState = new()
        {
            ActiveJobs = 1,
        };

        (int freeSlots, string status) =
            RegistrationBackgroundService.CalculateHeartbeatAvailability(
                workerState,
                maxConcurrentJobs: 4,
                customProfilesReady: false);

        freeSlots.Should().Be(0);
        status.Should().Be("Error");
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
