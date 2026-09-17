using System.Security;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Modules.Administration.Controllers.Admin;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Farm.Modules.Administration.Tests.Controllers;

/// <summary>
/// Item 3 coverage: corrupt/unavailable authorization state, replay, and journal I/O must
/// surface as 503 ProblemDetails from the host-update admin endpoints, never 409/500.
/// </summary>
public sealed class HostUpdateControllerAvailabilityTests
{
    [Fact]
    public async Task AuthorizeAsync_returns_503_when_authorization_state_is_corrupt()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolver(new InvalidDataException("authorization_state_invalid")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateManualAuthorizationResponse> result = await controller.AuthorizeAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task AuthorizeAsync_returns_503_when_authorization_state_access_is_denied_by_security_policy()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolver(new SecurityException("host_state_reparse_path_rejected")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateManualAuthorizationResponse> result = await controller.AuthorizeAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_replay_state_is_unavailable_during_resolution()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new ThrowingResolveManualResolver(new InvalidDataException("replay_unavailable")),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_resolution_reports_authorization_state_unavailable()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new FailingResolveManualResolver("authorization_state_unavailable"),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task ExecuteAsync_returns_503_when_journal_is_corrupt_during_execution()
    {
        HostUpdateController controller = new(
            new ThrowingExecutor(new InvalidDataException("journal_corrupt")),
            new SucceedingResolveManualResolver(),
            new NoopJournal(),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = await controller.ExecuteAsync(null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetStatus_returns_503_when_journal_is_corrupt()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new InvalidDataException("journal_corrupt")),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = controller.GetStatus("rel-1");

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task GetStatus_returns_503_when_journal_access_is_denied_by_security_policy()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new SecurityException("host_state_owner_mismatch")),
            new NoopRecovery());

        ActionResult<HostUpdateStatusResponse> result = controller.GetStatus("rel-1");

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    [Fact]
    public async Task RecoverAsync_returns_503_when_journal_is_unavailable()
    {
        HostUpdateController controller = new(
            new NoopExecutor(),
            new NoopResolver(),
            new ThrowingJournal(new IOException("journal_unavailable")),
            new NoopRecovery());

        ActionResult<HostUpdateRecoveryResult> result = await controller.RecoverAsync("rel-1", null, default);

        ObjectResult unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
    }

    private sealed class ThrowingResolver(Exception exception) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw exception;

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingResolveManualResolver(Exception exception) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw exception;
    }

    private sealed class FailingResolveManualResolver(string error) : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            Task.FromResult(HostUpdateExecutionResolutionResult.Fail(error));
    }

    private sealed class SucceedingResolveManualResolver : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) =>
            Task.FromResult(HostUpdateExecutionResolutionResult.Ok(Request(), Authorization()));

        private static HostUpdateExecutionRequest Request() => new("rel-1", 1, "sha256:" + new string('a', 64), new string('b', 40), HostUpdateExecutionChannel.Stable,
        [
            new("api", "linux-amd64", "sha256:" + new string('a', 64)),
        ])
        {
            RequestId = "request-1",
            TrustRoot = "trust-root",
            PolicyRevision = 1,
            PolicyFingerprint = "policy",
            HostPlatform = "linux-amd64",
            AuthorizationKind = HostUpdateAuthorizationKind.Manual,
        };

        private static HostUpdateManualAuthorizationRecord Authorization() => new(
            "auth-1",
            "candidate-fingerprint",
            "rel-1",
            1,
            "stable",
            "sha256:" + new string('a', 64),
            new string('b', 40),
            "trust-root",
            "linux-amd64",
            new HostUpdatePlatformDigests(
                "sha256:" + new string('a', 64),
                "sha256:" + new string('b', 64),
                "sha256:" + new string('c', 64),
                "sha256:" + new string('d', 64),
                "sha256:" + new string('e', 64),
                "sha256:" + new string('f', 64)),
            "replay-1",
            1,
            "policy",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            null,
            null,
            "binding-hash");
    }

    private sealed class ThrowingExecutor(Exception exception) : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw exception;
    }

    private sealed class ThrowingJournal(Exception exception) : IHostUpdateExecutionJournal
    {
        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => throw exception;

        public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException();
    }

    private sealed class NoopJournal : IHostUpdateExecutionJournal
    {
        public IReadOnlyList<HostUpdateExecutionActivity> Read(string releaseId) => Array.Empty<HostUpdateExecutionActivity>();

        public void Append(HostUpdateExecutionActivity activity) => throw new NotSupportedException();
    }

    private sealed class NoopRecovery : IHostUpdateRecoveryCoordinator
    {
        public Task<HostUpdateRecoveryResult> RecoverAsync(HostUpdateExecutionRequest failedRequest, IReadOnlyList<HostUpdateExecutionActivity> activities, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopExecutor : IHostUpdateExecutor
    {
        public Task<HostUpdateExecutionResult> ExecuteAsync(HostUpdateExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopResolver : IHostUpdateExecutionRequestResolver
    {
        public Task<HostUpdateManualAuthorizationResponse> AuthorizeCurrentAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
        public Task<HostUpdateExecutionResolutionResult> ResolveManualAsync(HostUpdateManualAuthorizationIntent intent, CancellationToken ct) => throw new NotSupportedException();
    }
}
