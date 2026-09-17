using System.Security.Claims;
using System.Text.Json;
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

namespace Farm.Modules.Identity.Tests.Controllers;

public class PasskeyControllerTests
{
    private readonly Mock<IAuthenticationService> _authService = new();
    private readonly Mock<ILoginAuditService> _loginAudit = new();
    private readonly Mock<IPasskeyService> _passkeySvc = new();
    private readonly Mock<ILogger<Farm.Modules.Identity.Controllers.AuthController>> _logger = new();
    private readonly Mock<IApiKeyExchangeService> _apiKeyExchangeService = new();

    private Farm.Modules.Identity.Controllers.AuthController CreateController(Guid? userId = null, string? username = null)
    {
        Farm.Modules.Identity.Controllers.AuthController controller = new(
            _authService.Object,
            _loginAudit.Object,
            _passkeySvc.Object,
            _logger.Object,
            _apiKeyExchangeService.Object);

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
    public async Task PasskeyRegisterBeginAsync_ValidRequest_ReturnsBrowserWireFormat()
    {
        CredentialCreateOptions fakeOptions = MakeCredentialCreateOptions();
        _passkeySvc
            .Setup(s => s.BeginRegistrationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeOptions);

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterBeginAsync(CancellationToken.None);

        ContentResult content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
        using JsonDocument json = JsonDocument.Parse(content.Content!);
        JsonElement root = json.RootElement;

        root.GetProperty("rp").GetProperty("id").GetString().Should().Be("localhost");
        root.GetProperty("attestation").GetString().Should().Be("none");
        root.GetProperty("challenge").GetString().Should().MatchRegex("^[A-Za-z0-9_-]+$");
        root.GetProperty("user").GetProperty("id").GetString().Should().MatchRegex("^[A-Za-z0-9_-]+$");

        JsonElement authenticatorSelection = root.GetProperty("authenticatorSelection");
        authenticatorSelection.GetProperty("residentKey").GetString().Should().Be("preferred");
        authenticatorSelection.GetProperty("userVerification").GetString().Should().Be("required");

        JsonElement.ArrayEnumerator parameters = root.GetProperty("pubKeyCredParams").EnumerateArray();
        JsonElement[] parameterValues = parameters.ToArray();
        parameterValues.Should().NotBeEmpty();
        parameterValues.Should().OnlyContain(parameter =>
            parameter.GetProperty("type").GetString() == "public-key" &&
            parameter.GetProperty("alg").ValueKind == JsonValueKind.Number);
        parameterValues.Select(parameter => parameter.GetProperty("alg").GetInt32()).Should().OnlyContain(algorithm => algorithm < 0);
    }

    [Fact]
    public async Task RegisterBegin_ServiceThrows_Returns400()
    {
        _passkeySvc
            .Setup(s => s.BeginRegistrationAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fido2 error"));

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
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

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
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

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
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

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyRegisterCompleteAsync(
            new AuthenticatorAttestationRawResponse(),
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    // ─── login/begin ─────────────────────────────────────────────────────────

    [Fact]
    public async Task PasskeyLoginBeginAsync_ValidRequest_ReturnsBrowserWireFormat()
    {
        AssertionOptions fakeOptions = MakeAssertionOptions();
        _passkeySvc
            .Setup(s => s.BeginLoginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeOptions);

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginBeginAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginBeginRequest("testuser"),
            CancellationToken.None);

        ContentResult content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
        using JsonDocument json = JsonDocument.Parse(content.Content!);

        JsonElement root = json.RootElement;
        root.GetProperty("rpId").GetString().Should().Be("localhost");
        root.GetProperty("userVerification").GetString().Should().Be("required");
        root.GetProperty("challenge").GetString().Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public async Task LoginBegin_MissingUsername_Returns400()
    {
        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginBeginAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginBeginRequest(string.Empty),
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

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
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

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task LoginComplete_BadAssertion_Returns422()
    {
        _passkeySvc
            .Setup(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Signature verification failed"));

        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginCompleteRequest("testuser", new AuthenticatorAssertionRawResponse()),
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [Fact]
    public async Task LoginComplete_MissingBody_Returns400()
    {
        Farm.Modules.Identity.Controllers.AuthController controller = CreateController();
        IActionResult result = await controller.PasskeyLoginCompleteAsync(
            new Farm.Modules.Identity.Controllers.PasskeyLoginCompleteRequest("testuser", null),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _passkeySvc.Verify(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAssertionRawResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static CredentialCreateOptions MakeCredentialCreateOptions()
    {
        Fido2 fido2 = CreateFido2();
        return fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = [1], Name = "u", DisplayName = "u" },
            ExcludeCredentials = [],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Required,
                ResidentKey = ResidentKeyRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
    }

    private static AssertionOptions MakeAssertionOptions()
    {
        Fido2 fido2 = CreateFido2();
        return fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
    }

    private static Fido2 CreateFido2() =>
        new(new Fido2Configuration
        {
            ServerDomain = "localhost",
            ServerName = "PrintFarmer",
            Origins = new HashSet<string> { "http://localhost:3000" },
        });
}
