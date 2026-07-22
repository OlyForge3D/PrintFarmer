using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.NfcDevices;
using Farm.Infrastructure.Services.SignalR;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Nfc;

public sealed class NfcTagServiceLifetimeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public NfcTagServiceLifetimeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OfflineQueue_PersistsAcrossScopes_WhenServiceIsSingleton()
    {
        var deviceId = Guid.NewGuid();
        var readAt = DateTime.UtcNow;

        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(x => x.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var hubMock = new Mock<IHubContext<NfcHub>>();
        hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        services.AddSingleton(hubMock.Object);
        services.AddSingleton<INfcTagService, NfcTagService>();

        await using var provider = services.BuildServiceProvider();

        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.NfcDevices.Add(new NfcDevice
            {
                Id = deviceId,
                Name = "Reader-1",
                LastHeartbeat = DateTime.UtcNow.AddMinutes(-10),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            db.NfcTagBindings.Add(new NfcTagBinding
            {
                Id = Guid.NewGuid(),
                TagUid = "AA:BB:CC:DD",
                SpoolId = 100,
                SpoolName = "Test Spool",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        INfcTagService firstRequestService;
        await using (var requestScope1 = provider.CreateAsyncScope())
        {
            firstRequestService = requestScope1.ServiceProvider.GetRequiredService<INfcTagService>();
            await firstRequestService.ProcessTagReadAsync("AA:BB:CC:DD", deviceId, null, readAt, CancellationToken.None);
        }

        INfcTagService secondRequestService;
        await using (var requestScope2 = provider.CreateAsyncScope())
        {
            secondRequestService = requestScope2.ServiceProvider.GetRequiredService<INfcTagService>();
            await secondRequestService.FlushOfflineQueueAsync(deviceId, CancellationToken.None);
        }

        secondRequestService.Should().BeSameAs(firstRequestService);
        clientProxyMock.Verify(
            x => x.SendCoreAsync(
                NfcHubEvents.TagRead,
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
