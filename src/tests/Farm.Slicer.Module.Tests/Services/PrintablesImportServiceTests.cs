using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Unit tests for Printables import: URL parsing, GraphQL client (mocked), and controller outcomes.
/// </summary>
public class PrintablesImportServiceTests
{
    // ── URL parsing ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.printables.com/model/12345", "12345")]
    [InlineData("https://www.printables.com/model/12345-my-cool-model", "12345")]
    [InlineData("https://printables.com/model/99999-slug-with-many-words", "99999")]
    [InlineData("https://www.printables.com/model/777777?ref=search", "777777")]
    [InlineData("https://printables.com/model/123", "123")]
    [InlineData("https://www.printables.com/model/123", "123")]
    public void ParseModelId_ValidUrl_ExtractsId(string url, string expectedId)
    {
        string modelId = PrintablesImportService.ParseModelId(url);
        _ = modelId.Should().Be(expectedId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://www.thingiverse.com/thing:12345")]
    [InlineData("https://www.printables.com/")]
    [InlineData("not-a-url")]
    [InlineData("https://evil.com/redirect?to=printables.com/model/123")]
    [InlineData("http://evil.com/redirect?to=printables.com/model/123")]
    [InlineData("https://printables.com.evil.com/model/123")]
    [InlineData("https://printables.evil.com/model/123")]
    [InlineData("http://www.printables.com/model/1")]
    [InlineData("https://www.printables.com/notmodel/123")]
    public void ParseModelId_InvalidUrl_ThrowsArgumentException(string url)
    {
        Action act = () => PrintablesImportService.ParseModelId(url);
        _ = act.Should().Throw<ArgumentException>();
    }

    // ── GraphQL client (mocked HTTP handler) ─────────────────────────────────

    private static HttpClient BuildMockedHttpClient(HttpStatusCode statusCode, string json)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        _ = handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchPreviewAsync_HappyPath_ReturnsPreviewDto()
    {
        const string json = """
            {
              "data": {
                "print": {
                  "id": "42",
                  "name": "Awesome Bracket",
                  "user": { "handle": "maker_jane" },
                  "license": { "name": "CC BY 4.0" },
                  "image": { "filePath": "https://media.printables.com/thumb.jpg" },
                  "stls": [
                    { "id": "s1", "name": "bracket_v2.stl", "fileSize": 102400 }
                  ]
                }
              }
            }
            """;

        PrintablesGraphQLClient client = new(BuildMockedHttpClient(HttpStatusCode.OK, json));
        PrintablesPreviewDto result = await client.FetchPreviewAsync("42", "https://www.printables.com/model/42", default);

        _ = result.ModelId.Should().Be("42");
        _ = result.Name.Should().Be("Awesome Bracket");
        _ = result.Creator.Should().Be("maker_jane");
        _ = result.License.Should().Be("CC BY 4.0");
        _ = result.ThumbnailUrl.Should().Be("https://media.printables.com/thumb.jpg");
        _ = result.Files.Should().HaveCount(1);
        _ = result.Files[0].Name.Should().Be("bracket_v2.stl");
        _ = result.Files[0].FileSize.Should().Be(102400);
    }

    [Fact]
    public async Task FetchPreviewAsync_GraphQLErrors_ThrowsPrintablesApiException()
    {
        const string json = """
            {
              "errors": [{ "message": "Cannot query field 'print'." }]
            }
            """;

        PrintablesGraphQLClient client = new(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.FetchPreviewAsync("1", "https://www.printables.com/model/1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*GraphQL error*");
    }

    [Fact]
    public async Task FetchPreviewAsync_NullPrintNode_ThrowsPrintablesApiException()
    {
        const string json = """{ "data": { "print": null } }""";

        PrintablesGraphQLClient client = new(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.FetchPreviewAsync("9", "https://www.printables.com/model/9", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task FetchPreviewAsync_HttpError_ThrowsPrintablesApiException()
    {
        PrintablesGraphQLClient client = new(BuildMockedHttpClient(HttpStatusCode.InternalServerError, "{}"));
        Func<Task> act = () => client.FetchPreviewAsync("1", "https://www.printables.com/model/1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*HTTP 500*");
    }

    // ── PrintablesImportService.PreviewAsync ──────────────────────────────────

    // ── Controller: preview endpoint ──────────────────────────────────────────

    private static PrintablesImportController BuildController(Mock<IPrintablesImportService> mockSvc)
    {
        Mock<ILogger<PrintablesImportController>> logger = new();
        return new PrintablesImportController(mockSvc.Object, logger.Object);
    }

    [Fact]
    public async Task PreviewAsync_HappyPath_Returns200WithDto()
    {
        PrintablesPreviewDto dto = new(
            ModelId: "42",
            Name: "Awesome Bracket",
            Creator: "maker_jane",
            License: "CC BY 4.0",
            ThumbnailUrl: "https://media.printables.com/thumb.jpg",
            SourceUrl: "https://www.printables.com/model/42-awesome-bracket",
            Files: new List<PrintablesFileEntryDto> { new("s1", "bracket.stl", 1024) });

        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.PreviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.PreviewAsync("https://www.printables.com/model/42-awesome-bracket", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesPreviewDto returned = Assert.IsType<PrintablesPreviewDto>(ok.Value);
        _ = returned.ModelId.Should().Be("42");
        _ = returned.Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task PreviewAsync_EmptyUrl_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        PrintablesImportController controller = BuildController(svcMock);

        IActionResult result = await controller.PreviewAsync("", CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PreviewAsync_InvalidUrl_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.PreviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Not a Printables URL."));

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.PreviewAsync("https://thingiverse.com/thing:1", CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task PreviewAsync_PrintablesApiError_Returns502()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.PreviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesApiException("Model not found on Printables."));

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.PreviewAsync("https://www.printables.com/model/99999", CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result);
        _ = obj.StatusCode.Should().Be(502);
    }
}
