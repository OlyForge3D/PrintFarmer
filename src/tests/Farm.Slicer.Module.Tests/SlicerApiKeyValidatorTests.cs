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
    public async Task ValidateSharedKeyAsync_BlankSlicerRegistryFallsThroughToWorkerSharedApiKey_AcceptsSharedApiKey()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("SlicerRegistry:ApiKey", string.Empty),
            new KeyValuePair<string, string?>("WorkerAuth:SharedApiKey", "the-key"));
        SlicerApiKeyValidator validator = new SlicerApiKeyValidator(
            configuration,
            new TestHostEnvironment("Production"),
            Mock.Of<ISlicersRepository>());

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
            Mock.Of<ISlicersRepository>());

        bool primaryResult = await validator.ValidateSharedKeyAsync("primary-key");
        bool secondaryResult = await validator.ValidateSharedKeyAsync("secondary-key");

        _ = primaryResult.Should().BeTrue();
        _ = secondaryResult.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSharedKeyAsync_MissingKeyWithoutExplicitDevelopmentOptIn_RejectsRequest()
    {
        IConfiguration configuration = CreateConfiguration();
        SlicerApiKeyValidator validator = new(
            configuration,
            new TestHostEnvironment("Development"),
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync(apiKey: null);

        _ = result.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateSharedKeyAsync_ExplicitDevelopmentOptInWithoutKey_AcceptsRequest()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(
                "WorkerAuth:AllowInsecureDevelopmentRegistration",
                "true"));
        SlicerApiKeyValidator validator = new(
            configuration,
            new TestHostEnvironment("Development"),
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync(apiKey: null);

        _ = result.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateSharedKeyAsync_DevelopmentOptInOutsideDevelopment_RejectsRequest()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(
                "WorkerAuth:AllowInsecureDevelopmentRegistration",
                "true"));
        SlicerApiKeyValidator validator = new(
            configuration,
            new TestHostEnvironment("Production"),
            Mock.Of<ISlicersRepository>());

        bool result = await validator.ValidateSharedKeyAsync(apiKey: null);

        _ = result.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_MissingSharedKey_ThrowsStartupException()
    {
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(),
            new TestHostEnvironment("Production"),
            new CapturingLogger<SlicerApiKeyStartupValidationService>());

        Func<Task> act = () => service.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*WorkerAuth:SharedKey*");
    }

    [Fact]
    public async Task StartAsync_DevelopmentOptInOutsideDevelopment_ThrowsStartupException()
    {
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(
                new KeyValuePair<string, string?>(
                    "WorkerAuth:AllowInsecureDevelopmentRegistration",
                    "true")),
            new TestHostEnvironment("Production"),
            new CapturingLogger<SlicerApiKeyStartupValidationService>());

        Func<Task> act = () => service.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*may only be enabled in the Development environment*");
    }

    [Fact]
    public async Task StartAsync_ExplicitDevelopmentOptIn_LogsCriticalWarning()
    {
        CapturingLogger<SlicerApiKeyStartupValidationService> logger = new();
        SlicerApiKeyStartupValidationService service = new(
            CreateConfiguration(
                new KeyValuePair<string, string?>(
                    "WorkerAuth:AllowInsecureDevelopmentRegistration",
                    "true")),
            new TestHostEnvironment("Development"),
            logger);

        await service.StartAsync(CancellationToken.None);

        _ = logger.Levels.Should().ContainSingle().Which.Should().Be(LogLevel.Critical);
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
            _ = state;
            _ = exception;
            _ = formatter;
            Levels.Add(logLevel);
        }
    }
}
