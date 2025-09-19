
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Farm.Web.Api.Tests;
using Farm.Web.Api.Tests.Infrastructure;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;

namespace Farm.Web.Api.Tests.Infrastructure;

public abstract class CustomDbHeavyTestBase : DbHeavyTestBase<Program>
{
    protected new CustomWebApplicationFactory _factory;

    protected CustomDbHeavyTestBase(CustomWebApplicationFactory factory)
        : base(factory)
    {
        _factory = factory;
    }

    // Expose mocks for convenience
    protected Mock<INetworkDiscoveryService> MockNetworkDiscoveryService => _factory.MockNetworkDiscoveryService;
    protected Mock<IMoonrakerClient> MockMoonrakerClient => _factory.MockMoonrakerClient;
    protected Mock<IPrusaLinkClient> MockPrusaLinkClient => _factory.MockPrusaLinkClient;
    protected Mock<ISdcpClient> MockSdcpClient => _factory.MockSdcpClient;
    protected Mock<ISlicerJobQueue> MockSlicerJobQueue => _factory.MockSlicerJobQueue;
    protected Mock<ISlicerFileStorage> MockSlicerFileStorage => _factory.MockSlicerFileStorage;
    protected Mock<ISlicerProgressNotifier> MockSlicerProgressNotifier => _factory.MockSlicerProgressNotifier;
    protected Mock<IModelAnalysisService> MockModelAnalysisService => _factory.MockModelAnalysisService;
}
