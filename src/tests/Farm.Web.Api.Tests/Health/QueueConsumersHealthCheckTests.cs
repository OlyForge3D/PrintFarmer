using Farm.Web.Api.Health;
using Farm.Web.Api.Infrastructure;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Health;

/// <summary>
/// Issue #3157: the <c>queue-consumers</c> <c>/health</c> entry must report the real liveness of
/// the queue consumer hosted services, never a constant Healthy.
/// </summary>
public sealed class QueueConsumersHealthCheckTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public async Task AllConsumersRunning_IsHealthy()
    {
        using var first = new LoopingService();
        using var second = new OtherLoopingService();
        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);

        try
        {
            HealthCheckResult result = await CheckAsync([first, second], Consumers(("first", typeof(LoopingService)), ("second", typeof(OtherLoopingService))));

            result.Status.Should().Be(HealthStatus.Healthy);
            result.Data.Should().Contain("first", QueueConsumersHealthCheck.Running)
                .And.Contain("second", QueueConsumersHealthCheck.Running);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConsumerNotRegistered_IsUnhealthy()
    {
        HealthCheckResult result = await CheckAsync([], Consumers(("first", typeof(LoopingService))));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.NotRegistered);
        result.Description.Should().Contain("first=notRegistered");
    }

    [Fact]
    public async Task ConsumerNeverStarted_IsUnhealthy()
    {
        using var service = new LoopingService();

        HealthCheckResult result = await CheckAsync([service], Consumers(("first", typeof(LoopingService))));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.NotStarted);
    }

    [Fact]
    public async Task ConsumerStopped_IsUnhealthy()
    {
        using var service = new LoopingService();
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        HealthCheckResult result = await CheckAsync([service], Consumers(("first", typeof(LoopingService))));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["first"].Should().BeOneOf(QueueConsumersHealthCheck.Stopped, QueueConsumersHealthCheck.Canceled);
    }

    [Fact]
    public async Task ConsumerLoopReturned_IsUnhealthy()
    {
        using var service = new ReturningService();
        await service.StartAsync(CancellationToken.None);
        WaitForExit(service);

        HealthCheckResult result = await CheckAsync([service], Consumers(("first", typeof(ReturningService))));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.Stopped);
    }

    [Fact]
    public async Task ConsumerFaulted_IsUnhealthy()
    {
        using var service = new FaultingService();
        await service.StartAsync(CancellationToken.None);
        WaitForExit(service);

        HealthCheckResult result = await CheckAsync([service], Consumers(("first", typeof(FaultingService))));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.Faulted);
    }

    [Fact]
    public async Task OneConsumerDown_IsUnhealthyAndNamesOnlyThatConsumer()
    {
        using var running = new LoopingService();
        await running.StartAsync(CancellationToken.None);

        try
        {
            HealthCheckResult result = await CheckAsync(
                [running],
                Consumers(("first", typeof(LoopingService)), ("second", typeof(OtherLoopingService))));

            result.Status.Should().Be(HealthStatus.Unhealthy);
            result.Data.Should().Contain("first", QueueConsumersHealthCheck.Running)
                .And.Contain("second", QueueConsumersHealthCheck.NotRegistered);
            result.Description.Should().Contain("second=notRegistered").And.NotContain("first=");
        }
        finally
        {
            await running.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConsumerThatIsNotABackgroundService_FailsClosed()
    {
        var hostedService = new Mock<IHostedService>();

        HealthCheckResult result = await CheckAsync(
            [hostedService.Object],
            Consumers(("first", hostedService.Object.GetType())));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.Unobservable);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    public async Task BackgroundServicesDisabled_IsDegradedNotHealthy(string flag)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [QueueConsumersHealthCheck.DisableBackgroundServicesKey] = flag })
            .Build();
        var check = new QueueConsumersHealthCheck([], configuration, Consumers(("first", typeof(LoopingService))));

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data.Should().Contain("first", QueueConsumersHealthCheck.Disabled);
    }

    [Fact]
    public void DefaultConsumers_AreBackgroundServicesWithUniqueCamelCaseKeys()
    {
        QueueConsumersHealthCheck.DefaultConsumers.Should().HaveCount(6);
        QueueConsumersHealthCheck.DefaultConsumers.Select(c => c.Key).Should().OnlyHaveUniqueItems();
        foreach (KeyValuePair<string, Type> consumer in QueueConsumersHealthCheck.DefaultConsumers)
        {
            consumer.Value.Should().BeAssignableTo<BackgroundService>();
            char.IsLower(consumer.Key[0]).Should().BeTrue(consumer.Key);
        }
    }

    [Fact]
    public void EntryName_IsStableAndMatchesRecoveryMatrixQueueFilter()
    {
        // The recovery matrix (#3100) selects /health entries matching /queue|dispatch|outbox|consumer/i.
        QueueConsumersHealthCheck.Name.Should().Be("queue-consumers");
    }

    [Fact]
    public void AddPrintFarmerHealthChecks_RegistersQueueConsumersEntry()
    {
        var services = new ServiceCollection();
        services.AddPrintFarmerHealthChecks();

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckServiceOptions options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        options.Registrations.Select(r => r.Name).Should().Contain(QueueConsumersHealthCheck.Name);
    }

    [Fact]
    public void ProductRegistration_HostsEveryRequiredConsumer()
    {
        var services = new ServiceCollection();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Production);

        services.AddPrintFarmerServices(EmptyConfiguration, environment.Object);
        services.AddPrintFarmerBackgroundServices(EmptyConfiguration);

        HashSet<Type> hostedTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!)
            .ToHashSet();
        foreach (KeyValuePair<string, Type> consumer in QueueConsumersHealthCheck.DefaultConsumers)
        {
            hostedTypes.Should().Contain(consumer.Value, consumer.Key);
        }
    }

    private static Task<HealthCheckResult> CheckAsync(
        IEnumerable<IHostedService> hostedServices,
        IReadOnlyList<KeyValuePair<string, Type>> consumers) =>
        new QueueConsumersHealthCheck(hostedServices, EmptyConfiguration, consumers).CheckHealthAsync(new HealthCheckContext());

    private static List<KeyValuePair<string, Type>> Consumers(params (string Key, Type Type)[] consumers) =>
        consumers.Select(c => new KeyValuePair<string, Type>(c.Key, c.Type)).ToList();

    private class LoopingService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private sealed class OtherLoopingService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private static void WaitForExit(BackgroundService service) =>
        SpinWait.SpinUntil(() => service.ExecuteTask is { IsCompleted: true }, TimeSpan.FromSeconds(10)).Should().BeTrue();

    private sealed class ReturningService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken) => await Task.Yield();
    }

    private sealed class FaultingService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("consumer crashed");
        }
    }
}
