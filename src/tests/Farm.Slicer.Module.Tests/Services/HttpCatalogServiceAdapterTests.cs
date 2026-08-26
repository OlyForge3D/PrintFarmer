extern alias SlicerHost;
using System.Net;
using System.Text;
using Farm.Slicer.Module.Services.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using HttpCatalogServiceAdapter =
    SlicerHost::Farm.Slicer.Host.Services.HttpCatalogServiceAdapter;
using HttpPrinterLookupService =
    SlicerHost::Farm.Slicer.Host.Services.HttpPrinterLookupService;
using MainApiServiceAuthenticationHandler =
    SlicerHost::Farm.Slicer.Host.Services.MainApiServiceAuthenticationHandler;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class HttpCatalogServiceAdapterTests
{
    private const string SharedKey = "test-slicer-host-key";

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

    [Fact]
    public async Task GetManufacturerNamesAsync_Request_CarriesConfiguredServiceKey()
    {
        string? presentedKey = null;
        string? requestPath = null;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        HttpCatalogServiceAdapter service = CreateCatalogService(cache, request =>
        {
            requestPath = request.RequestUri?.AbsolutePath;
            presentedKey = request.Headers
                .GetValues(Farm.Infrastructure.SlicerHostLookupContract.ApiKeyHeaderName)
                .Single();
            return JsonResponse("[]");
        });

        IReadOnlyList<string> result = await service.GetManufacturerNamesAsync();

        result.Should().BeEmpty();
        presentedKey.Should().Be(SharedKey);
        requestPath.Should().Be(
            $"/{Farm.Infrastructure.SlicerHostLookupContract.ManufacturersPath}");
    }

    [Fact]
    public async Task GetManufacturerNamesAsync_Unauthorized_ThrowsAuthenticationException()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        HttpCatalogServiceAdapter service = CreateCatalogService(
            cache,
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        Func<Task> act = async () => await service.GetManufacturerNamesAsync();

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .Where(exception => exception.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPrinterByIdAsync_Forbidden_ThrowsAuthenticationException()
    {
        IHttpClientFactory clientFactory = CreateAuthenticatedClientFactory(
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new HttpPrinterLookupService(
            clientFactory,
            cache,
            Mock.Of<ILogger<HttpPrinterLookupService>>());

        Func<Task> act = async () => await service.GetPrinterByIdAsync(Guid.NewGuid());

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .Where(exception => exception.StatusCode == HttpStatusCode.Forbidden);
    }

    private static HttpCatalogServiceAdapter CreateCatalogService(
        IMemoryCache cache,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(
            CreateAuthenticatedClientFactory(responseFactory),
            cache,
            Mock.Of<ILogger<HttpCatalogServiceAdapter>>());

    private static IHttpClientFactory CreateAuthenticatedClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkerAuthConfiguration.SharedKeyPath] = SharedKey,
            })
            .Build();
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        _ = factory
            .Setup(value => value.CreateClient("MainApi"))
            .Returns(() =>
            {
                var authenticationHandler =
                    new MainApiServiceAuthenticationHandler(configuration)
                    {
                        InnerHandler = new StubHttpMessageHandler(responseFactory),
                    };
                return new HttpClient(authenticationHandler)
                {
                    BaseAddress = new Uri("http://main-api/"),
                };
            });
        return factory.Object;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

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
