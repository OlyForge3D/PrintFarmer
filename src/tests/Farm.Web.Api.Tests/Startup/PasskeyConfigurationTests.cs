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
}
