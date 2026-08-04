using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests;

public sealed class SlicerApiKeyValidatorTests
{
    [Fact]
    public async Task ValidateSharedKeyAsync_CanonicalKeyConfigured_AcceptsMatchingKey()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", "the-key"));
        SlicerApiKeyValidator validator = new SlicerApiKeyValidator(
            configuration,
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync("the-key");

        _ = result.Should().BeTrue();
    }

    [Theory]
    [InlineData("WorkerAuth:SharedApiKey")]
    [InlineData("SlicerRegistry:ApiKey")]
    [InlineData("WORKER_SHARED_API_KEY")]
    [InlineData("SLICER_REGISTRATION_KEY")]
    public async Task ValidateSharedKeyAsync_LegacyAliasOnly_RejectsKey(string legacyPath)
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(legacyPath, "legacy-key"));
        SlicerApiKeyValidator validator = new SlicerApiKeyValidator(
            configuration,
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync("legacy-key");

        _ = result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSharedKeyAsync_MissingKey_RejectsRequest()
    {
        IConfiguration configuration = CreateConfiguration();
        SlicerApiKeyValidator validator = new(
            configuration,
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync(apiKey: null);

        _ = result.Should().BeFalse();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task StartAsync_MissingSharedKeyInAnyEnvironment_ThrowsStartupException(
        string environmentName)
    {
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(),
            new TestHostEnvironment(environmentName),
            new CapturingLogger<SlicerApiKeyStartupValidationService>());

        Func<Task> act = () => service.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WorkerAuth:SharedKey*");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task StartAsync_BlankSharedKeyInAnyEnvironment_ThrowsStartupException(
        string environmentName)
    {
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(
                new KeyValuePair<string, string?>(
                    "WorkerAuth:SharedKey",
                    "   ")),
            new TestHostEnvironment(environmentName),
            new CapturingLogger<SlicerApiKeyStartupValidationService>());

        Func<Task> act = () => service.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WorkerAuth:SharedKey*");
    }

    [Fact]
    public async Task StartAsync_ConfiguredKey_LogsSourceWithoutKeyMaterial()
    {
        const string key = "sensitive-test-registration-key";
        CapturingLogger<SlicerApiKeyStartupValidationService> logger = new();
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(
                new KeyValuePair<string, string?>(
                    "WorkerAuth:SharedKey",
                    key)),
            new TestHostEnvironment("Testing"),
            logger);

        await service.StartAsync(CancellationToken.None);

        _ = logger.Levels.Should().ContainSingle().Which.Should().Be(LogLevel.Information);
        _ = logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("WorkerAuth:SharedKey")
            .And.Contain("MemoryConfigurationProvider")
            .And.NotContain(key);
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
