// <copyright file="PrintersControllerHistoryContractTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Sockets;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class PrintersControllerHistoryContractTests : IAsyncLifetime
{
    private readonly Mock<IPrintersService> _printers = new(MockBehavior.Strict);
    private readonly HistoryContractFactory _factory;
    private readonly HttpClient _client;

    public PrintersControllerHistoryContractTests()
    {
        _factory = new HistoryContractFactory(_printers);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task HistoryRoutes_UnsupportedBackend_ReturnBadRequest()
    {
        Guid printerId = Guid.NewGuid();
        _printers.Setup(service => service.GetHistoryListAsync(
                printerId,
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("unsupported"));
        _printers.Setup(service => service.GetHistoryJobAsync(
                printerId,
                "job-1",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("unsupported"));

        HttpResponseMessage list = await _client.GetAsync(
            $"/api/printers/{printerId}/history");
        HttpResponseMessage detail = await _client.GetAsync(
            $"/api/printers/{printerId}/history/job-1");

        list.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        detail.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false, typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(false, typeof(SocketException), HttpStatusCode.BadGateway)]
    [InlineData(false, typeof(TimeoutException), HttpStatusCode.RequestTimeout)]
    [InlineData(true, typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(true, typeof(SocketException), HttpStatusCode.BadGateway)]
    [InlineData(true, typeof(TimeoutException), HttpStatusCode.RequestTimeout)]
    public async Task HistoryRoutes_ClassifiedProviderFailure_ReturnExpectedHttpStatus(
        bool detailRoute,
        Type exceptionType,
        HttpStatusCode expectedStatus)
    {
        Guid printerId = Guid.NewGuid();
        Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
        if (detailRoute)
        {
            _printers.Setup(service => service.GetHistoryJobAsync(
                    printerId,
                    "job-1",
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }
        else
        {
            _printers.Setup(service => service.GetHistoryListAsync(
                    printerId,
                    It.IsAny<int?>(),
                    It.IsAny<int?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<DateTime?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }

        string path = detailRoute
            ? $"/api/printers/{printerId}/history/job-1"
            : $"/api/printers/{printerId}/history";
        HttpResponseMessage response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(expectedStatus);
    }

    private sealed class HistoryContractFactory(Mock<IPrintersService> printers)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers.Object);
            });
        }
    }
}
