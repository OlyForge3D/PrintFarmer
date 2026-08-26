using System.Net;
using Farm.OrcaSlicer.Worker.Controllers;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class WorkerAuthenticationTests
{
    [Fact]
    public async Task SendAsync_RegisteredWorker_AttachesBoundIdentityAndKey()
    {
        Guid serviceId = Guid.NewGuid();
        var state = new WorkerStateService();
        state.SetRegisteredService(serviceId, "registered-worker-key");
        var innerHandler = new CapturingHandler();
        using var authenticationHandler = new WorkerApiAuthenticationHandler(state)
        {
            InnerHandler = innerHandler,
        };
        using var client = new HttpClient(authenticationHandler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://slicer.test/api/slice");
        request.Headers.Add(WorkerLeaseHeaders.WorkerId, Guid.NewGuid().ToString());
        request.Headers.Add(WorkerLeaseHeaders.WorkerKey, "wrong-key");

        using HttpResponseMessage response = await client.SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = innerHandler.WorkerIds.Should().ContainSingle().Which
            .Should().Be(serviceId.ToString());
        _ = innerHandler.WorkerKeys.Should().ContainSingle().Which
            .Should().Be("registered-worker-key");
    }

    [Fact]
    public async Task SendAsync_UnregisteredWorker_ThrowsBeforeSending()
    {
        var innerHandler = new CapturingHandler();
        using var authenticationHandler =
            new WorkerApiAuthenticationHandler(new WorkerStateService())
            {
                InnerHandler = innerHandler,
            };
        using var client = new HttpClient(authenticationHandler);

        Func<Task> act = async () =>
            _ = await client.GetAsync("https://slicer.test/api/slice");

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Authenticated worker identity is unavailable*");
        _ = innerHandler.RequestCount.Should().Be(0);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void ValidateWorkerAuthenticationConfiguration_MissingKeyInAnyEnvironment_Throws(
        string environmentName)
    {
        IConfiguration configuration = CreateConfiguration();

        Action validate = () => Program.ValidateWorkerAuthenticationConfiguration(
            configuration,
            new TestHostEnvironment(environmentName),
            NullLogger.Instance);

        _ = validate.Should().Throw<InvalidOperationException>()
            .WithMessage("*WorkerAuth:SharedKey*");
    }

    [Fact]
    public void ValidateWorkerAuthenticationConfiguration_ConfiguredKey_LogsSourceWithoutKeyMaterial()
    {
        const string key = "sensitive-test-registration-key";
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>("WorkerAuth:SharedKey", key));
        var logger = new CapturingLogger();

        Program.ValidateWorkerAuthenticationConfiguration(
            configuration,
            new TestHostEnvironment("Testing"),
            logger);

        _ = logger.Messages.Should().ContainSingle()
            .Which.Should().Contain("WorkerAuth:SharedKey")
            .And.Contain("MemoryConfigurationProvider")
            .And.NotContain(key);
    }

    [Fact]
    public void WorkerSharedKeyValidator_PresentedKey_UsesExistingBootstrapCredential()
    {
        IConfiguration configuration = CreateConfiguration(
            new KeyValuePair<string, string?>(
                "WorkerAuth:SharedKey",
                "worker-management-key"));
        var validator = new WorkerSharedKeyValidator(configuration);

        validator.Validate("worker-management-key").Should().BeTrue();
        validator.Validate("wrong-key").Should().BeFalse();
        validator.Validate(null).Should().BeFalse();
    }

    private static IConfiguration CreateConfiguration(
        params KeyValuePair<string, string?>[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string[] WorkerIds { get; private set; } = [];

        public string[] WorkerKeys { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            RequestCount++;
            WorkerIds = request.Headers.GetValues(WorkerLeaseHeaders.WorkerId).ToArray();
            WorkerKeys = request.Headers.GetValues(WorkerLeaseHeaders.WorkerKey).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } =
            "Farm.OrcaSlicer.Worker.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class CapturingLogger : ILogger
    {
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
            _ = logLevel;
            _ = eventId;
            Messages.Add(formatter(state, exception));
        }
    }
}
