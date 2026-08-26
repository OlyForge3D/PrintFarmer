extern alias SlicerHost;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using HttpCatalogServiceAdapter =
    SlicerHost::Farm.Slicer.Host.Services.HttpCatalogServiceAdapter;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class HttpCatalogServiceAdapterTests
{
    [Fact]
    public async Task InvalidateModelAliasesAsync_RefreshesPreviouslyCachedEmptyResult()
    {
        Guid modelId = Guid.NewGuid();
        int requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            string json = requestCount == 1
                ? "[]"
                : $$"""
                    [{
                      "id": "{{Guid.NewGuid()}}",
                      "printerModelId": "{{modelId}}",
                      "slicerModelName": "Micron 180",
                      "slicerType": "OrcaSlicer"
                    }]
                    """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        var httpClientFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        _ = httpClientFactory
            .Setup(factory => factory.CreateClient("MainApi"))
            .Returns(() => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://main-api/")
            });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new HttpCatalogServiceAdapter(
            httpClientFactory.Object,
            cache,
            Mock.Of<ILogger<HttpCatalogServiceAdapter>>());

        IReadOnlyList<Farm.Infrastructure.Dtos.SlicerModelAliasDto> initial =
            await service.GetModelAliasesAsync(modelId);
        IReadOnlyList<Farm.Infrastructure.Dtos.SlicerModelAliasDto> cached =
            await service.GetModelAliasesAsync(modelId);
        await service.InvalidateModelAliasesAsync(modelId);
        IReadOnlyList<Farm.Infrastructure.Dtos.SlicerModelAliasDto> refreshed =
            await service.GetModelAliasesAsync(modelId);

        initial.Should().BeEmpty();
        cached.Should().BeEmpty();
        refreshed.Should().ContainSingle(alias => alias.SlicerModelName == "Micron 180");
        requestCount.Should().Be(2);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}
