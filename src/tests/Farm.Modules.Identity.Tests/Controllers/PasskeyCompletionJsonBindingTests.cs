using System.Net;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Tests;
using Fido2NetLib;
using Fido2NetLib.Objects;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using Xunit;

namespace Farm.Modules.Identity.Tests.Controllers;

/// <summary>
/// Regression coverage for issue #2763: passkey completion endpoints must accept the real
/// WebAuthn browser wire value <c>"type":"public-key"</c>. These tests send literal, browser-shaped
/// JSON through the actual MVC HTTP pipeline (routing, authorization, model binding) rather than
/// calling the controller method directly, so they exercise exactly the code path that a real
/// browser ceremony hits - unlike <see cref="PasskeyControllerTests"/>, which constructs
/// <c>AuthenticatorAttestationRawResponse</c>/<c>AuthenticatorAssertionRawResponse</c> objects
/// directly in C# and never goes through JSON deserialization.
/// </summary>
[Trait("Category", "Integration")]
public class PasskeyCompletionJsonBindingTests : IClassFixture<PasskeyCompletionJsonBindingTests.Factory>, IAsyncLifetime
{
    // Literal, browser-shaped WebAuthn JSON payloads. Byte fields carry the Fido2NetLib
    // Base64Url-converter wire format; the actual byte content is irrelevant to this test - only
    // "type":"public-key" needs to bind correctly. See Fido2InboundJsonOptions for why the global
    // JsonStringEnumConverter previously rejected this literal browser payload (issue #2763).
    private const string AttestationJson = """
        {
          "id": "AQIDBA",
          "rawId": "AQIDBA",
          "type": "public-key",
          "response": {
            "attestationObject": "AQIDBA",
            "clientDataJSON": "AQIDBA",
            "transports": []
          },
          "clientExtensionResults": {}
        }
        """;

    private const string AssertionJson = """
        {
          "id": "AQIDBA",
          "rawId": "AQIDBA",
          "type": "public-key",
          "response": {
            "authenticatorData": "AQIDBA",
            "clientDataJSON": "AQIDBA",
            "signature": "AQIDBA",
            "userHandle": "AQIDBA"
          },
          "clientExtensionResults": {}
        }
        """;

    public class Factory : CustomWebApplicationFactory
    {
        public Mock<IPasskeyService> PasskeyServiceMock { get; } = new();

        public Factory() : base(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasskeyService>();
                services.AddSingleton(PasskeyServiceMock.Object);
            });
        }
    }

    private readonly Factory _factory;

    public PasskeyCompletionJsonBindingTests(Factory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        _factory.PasskeyServiceMock.Reset();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task RegisterComplete_LiteralBrowserJson_BindsPublicKeyTypeAndReturns200()
    {
        AuthenticatorAttestationRawResponse? captured = null;
        _factory.PasskeyServiceMock
            .Setup(s => s.CompleteRegistrationAsync(
                It.IsAny<string>(),
                It.IsAny<AuthenticatorAttestationRawResponse>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, AuthenticatorAttestationRawResponse, CancellationToken>((_, response, _) => captured = response)
            .ReturnsAsync((new RegisteredPublicKeyCredential
            {
                Id = [1, 2, 3],
                PublicKey = [4, 5, 6],
                Type = PublicKeyCredentialType.PublicKey,
            }, 42));

        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "passkey-json-register",
            email: "passkey-json-register@example.com");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/auth/passkey/register/complete",
            JsonContent(AttestationJson));

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"binding should succeed for the real WebAuthn wire value; body: {body}");

        captured.Should().NotBeNull();
        captured!.Type.Should().Be(PublicKeyCredentialType.PublicKey);
        captured.Id.Should().Be("AQIDBA");
    }

    [Fact]
    public async Task LoginComplete_LiteralBrowserJson_BindsPublicKeyTypeAndReturns200()
    {
        AuthenticatorAssertionRawResponse? captured = null;
        _factory.PasskeyServiceMock
            .Setup(s => s.CompleteLoginAsync(
                It.IsAny<string>(),
                It.IsAny<AuthenticatorAssertionRawResponse>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, AuthenticatorAssertionRawResponse, CancellationToken>((_, response, _) => captured = response)
            .ReturnsAsync(new AuthenticationResult(true, Token: "jwt.token.value"));

        using HttpClient client = _factory.CreateClient();
        string payload = $$"""{"username":"passkey-json-login","assertionResponse":{{AssertionJson}}}""";

        using HttpResponseMessage response = await client.PostAsync(
            "/api/auth/passkey/login/complete",
            JsonContent(payload));

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: $"binding should succeed for the real WebAuthn wire value; body: {body}");

        captured.Should().NotBeNull();
        captured!.Type.Should().Be(PublicKeyCredentialType.PublicKey);
        captured.Id.Should().Be("AQIDBA");
    }

    [Fact]
    public async Task RegisterComplete_MalformedJson_Returns400ViaModelState()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            username: "passkey-json-register-bad",
            email: "passkey-json-register-bad@example.com");

        using HttpResponseMessage response = await client.PostAsync(
            "/api/auth/passkey/register/complete",
            JsonContent("{ not valid json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.PasskeyServiceMock.Verify(
            s => s.CompleteRegistrationAsync(It.IsAny<string>(), It.IsAny<AuthenticatorAttestationRawResponse>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
