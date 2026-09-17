using Farm.Web.Api.Infrastructure;
using Fido2NetLib;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

public sealed class PasskeyConfigurationTests
{
    [Fact]
    public void CreateFido2Configuration_MissingValues_UsesLocalDevelopmentDefaults()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Fido2Configuration result = ServiceCollectionExtensions.CreateFido2Configuration(configuration);

        result.ServerDomain.Should().Be("localhost");
        result.ServerName.Should().Be("PrintFarmer");
        result.Origins.Should().Equal("http://localhost:3000");
    }

    [Fact]
    public void CreateFido2Configuration_DeploymentValues_UsesConfiguredRelyingParty()
    {
        Dictionary<string, string?> values = new()
        {
            ["WebAuthn:RelyingPartyId"] = "pfarm.example.com",
            ["WebAuthn:RelyingPartyName"] = "Example PrintFarmer",
            ["WebAuthn:Origin"] = "https://pfarm.example.com",
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Fido2Configuration result = ServiceCollectionExtensions.CreateFido2Configuration(configuration);

        result.ServerDomain.Should().Be("pfarm.example.com");
        result.ServerName.Should().Be("Example PrintFarmer");
        result.Origins.Should().Equal("https://pfarm.example.com");
    }

    [Fact]
    public void CreateFido2Configuration_WhitespaceValues_UsesLocalDevelopmentDefaults()
    {
        Dictionary<string, string?> values = new()
        {
            ["WebAuthn:RelyingPartyId"] = " ",
            ["WebAuthn:RelyingPartyName"] = "\t",
            ["WebAuthn:Origin"] = "\r\n",
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Fido2Configuration result = ServiceCollectionExtensions.CreateFido2Configuration(configuration);

        result.ServerDomain.Should().Be("localhost");
        result.ServerName.Should().Be("PrintFarmer");
        result.Origins.Should().Equal("http://localhost:3000");
    }

    [Theory]
    [InlineData("https://pfarm.example.com", "pfarm.example.com")]
    [InlineData("https://console.pfarm.example.com", "pfarm.example.com")]
    [InlineData("http://localhost:3000", "localhost")]
    public void CreateFido2Configuration_ValidOrigin_UsesConfiguredRelyingParty(string origin, string relyingPartyId)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAuthn:RelyingPartyId"] = relyingPartyId,
                ["WebAuthn:Origin"] = origin,
            })
            .Build();

        Fido2Configuration result = ServiceCollectionExtensions.CreateFido2Configuration(configuration);

        result.ServerDomain.Should().Be(relyingPartyId);
        result.Origins.Should().Equal(origin);
    }

    [Theory]
    [InlineData("https://pfarm.example.com", "https://pfarm.example.com")]
    [InlineData("127.0.0.1", "http://127.0.0.1:3000")]
    [InlineData("pfarm.example.com", "http://pfarm.example.com")]
    [InlineData("pfarm.example.com", "https://pfarm.example.com/path")]
    [InlineData("pfarm.example.com", "https://pfarm.example.com?query=value")]
    [InlineData("pfarm.example.com", "https://other.example.com")]
    public void CreateFido2Configuration_InvalidWebAuthnSettings_FailsFast(string relyingPartyId, string origin)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAuthn:RelyingPartyId"] = relyingPartyId,
                ["WebAuthn:Origin"] = origin,
            })
            .Build();

        Action action = () => ServiceCollectionExtensions.CreateFido2Configuration(configuration);

        action.Should().Throw<InvalidOperationException>();
    }
}
