using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public sealed class WebhookDeliveryFenceTests
{
    [Fact]
    public async Task WebhookService_AcknowledgesPauseBeforeStartingQueuedDelivery()
    {
        var fenceFlag = new WebhookDeliveryFenceFlag();
        await fenceFlag.RequestPauseAsync(CancellationToken.None);
        using ServiceProvider serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = new WebhookService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ISensitiveDataProtector>(),
            NullLogger<WebhookService>.Instance,
            fenceFlag);

        service.Enqueue("printer.updated", new { printerId = 1 });
        await service.StartAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!await fenceFlag.IsPausedAsync(cts.Token))
            {
                await Task.Delay(25, cts.Token);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(await fenceFlag.IsPausedAsync(CancellationToken.None));
    }
}
