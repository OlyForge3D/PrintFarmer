using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public sealed class SlicerApiKeyValidatorTests
{
    [Fact]
    public async Task ValidateSharedKeyAsync_BlankSlicerRegistryFallsThroughToWorkerSharedApiKey_AcceptsSharedApiKey()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerRegistry:ApiKey", string.Empty),
            new KeyValuePair<string, string?>("WorkerAuth:SharedApiKey", "the-key"));
        SlicerApiKeyValidator validator = new SlicerApiKeyValidator(
            configuration,
            new TestHostEnvironment("Production"),
            Mock.Of<ISlicersRepository>(),
            NullLogger<SlicerApiKeyValidator>.Instance);

        bool result = await validator.ValidateSharedKeyAsync("the-key");

        _ = result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSharedKeyAsync_SharedKeyAndSharedApiKeyConfigured_UsesSharedKeyPrecedence()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "primary-key"),
            new KeyValuePair<string, string?>("WorkerAuth:SharedApiKey", "secondary-key"),
            new KeyValuePair<string, string?>("SlicerRegistry:ApiKey", "legacy-key"));
        SlicerApiKeyValidator validator = new SlicerApiKeyValidator(
            configuration,
            new TestHostEnvironment("Production"),
            Mock.Of<ISlicersRepository>(),
            NullLogger<SlicerApiKeyValidator>.Instance);

        bool primaryResult = await validator.ValidateSharedKeyAsync("primary-key");
        bool secondaryResult = await validator.ValidateSharedKeyAsync("secondary-key");

        _ = primaryResult.Should().BeTrue();
        _ = secondaryResult.Should().BeFalse();
    }

    private static IConfiguration CreateConfiguration(params KeyValuePair<string, string?>[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Farm.Slicer.Module.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
