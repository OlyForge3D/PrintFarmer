using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Authentication;
using Fido2NetLib;
using Fido2NetLib.Objects;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class PasskeyControllerTests
{
    private readonly Mock<IAuthenticationService> _authService = new();
    private readonly Mock<ILoginAuditService> _loginAudit = new();
    private readonly Mock<IPasskeyService> _passkeySvc = new();
    private readonly Mock<ILogger<Farm.Web.Api.Controllers.AuthController>> _logger = new();

    private Farm.Web.Api.Controllers.AuthController CreateController(Guid? userId = null, string? username = null)
    {
        Farm.Web.Api.Controllers.AuthController controller = new(
            _authService.Object,
            _loginAudit.Object,
            _passkeySvc.Object,
            _logger.Object);

        List<Claim> claims = [
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Name, username ?? "testuser"),
        ];
        ClaimsIdentity identity = new(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    // ─── register/begin ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterBegin_HappyPath_Returns200WithOptions()
    {
        CredentialCreateOptions fakeOptions = MakeCredentialCreateOptions();
        _passkeySvc
            .Setup(s => s.BeginRegistrationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeOptions);

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterBeginAsync(CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fakeOptions);
    }

    [Fact]
    public async Task RegisterBegin_ServiceThrows_Returns400()
    {
        _passkeySvc
            .Setup(s => s.BeginRegistrationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fido2 error"));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterBeginAsync(CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ─── register/complete ───────────────────────────────────────────────────

    [Fact]
    public async Task RegisterComplete_HappyPath_Returns200()
    {
        RegisteredPublicKeyCredential fakeCredential = new() { Id = [1, 2, 3] };
        _passkeySvc
            .Setup(s => s.CompleteRegistrationAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAttestationRawResponse>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((fakeCredential, 42));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterCompleteAsync(
            new AuthenticatorAttestationRawResponse(),
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task RegisterComplete_ChallengeNotFound_Returns400()
    {
        _passkeySvc
            .Setup(s => s.CompleteRegistrationAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAttestationRawResponse>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PasskeyChallengeNotFoundException("No pending challenge"));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterCompleteAsync(
            new AuthenticatorAttestationRawResponse(),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task RegisterComplete_BadAttestation_Returns422()
    {
        _passkeySvc
            .Setup(s => s.CompleteRegistrationAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAttestationRawResponse>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Invalid attestation signature"));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterCompleteAsync(
            new AuthenticatorAttestationRawResponse(),
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    // ─── login/begin ─────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginBegin_HappyPath_Returns200WithOptions()
    {
        AssertionOptions fakeOptions = MakeAssertionOptions();
        _passkeySvc
            .Setup(s => s.BeginLoginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeOptions);

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginBeginAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginBeginRequest("testuser"),
            CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(fakeOptions);
    }

    [Fact]
    public async Task LoginBegin_MissingUsername_Returns400()
    {
        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginBeginAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginBeginRequest(string.Empty),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _passkeySvc.Verify(s => s.BeginLoginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── login/complete ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoginComplete_HappyPath_Returns200()
    {
        AuthenticationResult successResult = new(true, Token: "jwt.token.here");
        _passkeySvc
            .Setup(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(successResult);

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
            CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(successResult);
    }

    [Fact]
    public async Task LoginComplete_ChallengeReplay_Returns400()
    {
        _passkeySvc
            .Setup(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PasskeyChallengeNotFoundException("Challenge already used"));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task LoginComplete_BadAssertion_Returns422()
    {
        _passkeySvc
            .Setup(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Signature verification failed"));

        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task LoginComplete_MissingBody_Returns400()
    {
        Farm.Web.Api.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Web.Api.Controllers.PasskeyLoginCompleteRequest("testuser", null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _passkeySvc.Verify(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static CredentialCreateOptions MakeCredentialCreateOptions() =>
        new()
        {
            Rp = new PublicKeyCredentialRpEntity("localhost", "PrintFarmer", null),
            User = new Fido2User { Id = [1], Name = "u", DisplayName = "u" },
            Challenge = [42],
            PubKeyCredParams = [],
        };

    private static AssertionOptions MakeAssertionOptions() =>
        new() { Challenge = [42], RpId = "localhost" };
}
