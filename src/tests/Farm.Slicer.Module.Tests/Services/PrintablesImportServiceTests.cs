using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    [InlineData("ftp://www.printables.com/model/12345")]
    [InlineData("https://evilprintables.com/model/12345")]
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

    private static PrintablesGraphQLClient BuildClient(HttpClient httpClient, PrintablesGraphQlOptions? options = null)
    {
        return new PrintablesGraphQLClient(
            httpClient,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options ?? new PrintablesGraphQlOptions()),
            Mock.Of<ILogger<PrintablesGraphQLClient>>());
    }

    private static HttpClient BuildThrowingHttpClient(HttpRequestException exception)
    {
        Mock<HttpMessageHandler> handler = new(MockBehavior.Strict);
        _ = handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);

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

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
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

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.FetchPreviewAsync("1", "https://www.printables.com/model/1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*GraphQL error*");
    }

    [Fact]
    public async Task FetchPreviewAsync_NullPrintNode_ThrowsPrintablesApiException()
    {
        const string json = """{ "data": { "print": null } }""";

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.FetchPreviewAsync("9", "https://www.printables.com/model/9", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*missing required path*");
    }

    [Fact]
    public async Task FetchPreviewAsync_HttpError_ThrowsPrintablesApiException()
    {
        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.InternalServerError, "{}"));
        Func<Task> act = () => client.FetchPreviewAsync("1", "https://www.printables.com/model/1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*HTTP 500*");
    }

    [Fact]
    public async Task GetUserCollectionsAsync_WhenCalledTwice_UsesCache()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "16", "handle": "maker_jane", "publicUsername": "Maker Jane", "avatarFilePath": "avatars/u1.png" }
                  ]
                }
              }
            }
            """,
            """
            {
              "data": {
                "userCollections": [
                  {
                    "id": "c1",
                    "name": "Favorites",
                    "printsCount": 12,
                    "thumbnails": [
                      { "image": { "filePath": "media/c1.jpg" } }
                    ]
                  }
                ]
              }
            }
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler), new PrintablesGraphQlOptions { CacheTtlSeconds = 300 });

        IReadOnlyList<PrintablesCollectionDto> first = await client.GetUserCollectionsAsync("maker_jane", CancellationToken.None);
        IReadOnlyList<PrintablesCollectionDto> second = await client.GetUserCollectionsAsync("maker_jane", CancellationToken.None);

        _ = first.Should().HaveCount(1);
        _ = second.Should().HaveCount(1);
        _ = handler.PostCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchModelsAsync_HappyPath_ReturnsMappedPagedResult()
    {
        const string json = """
            {
              "data": {
                "searchPrints2": {
                  "totalCount": 1,
                  "items": [
                    {
                      "id": "42",
                      "name": "Awesome Bracket",
                      "slug": "awesome-bracket",
                      "summary": "A sturdy part",
                      "likesCount": 55,
                      "downloadCount": 1000,
                      "user": { "handle": "maker_jane" },
                      "image": { "filePath": "https://media.printables.com/thumb.jpg" }
                    }
                  ]
                }
              }
            }
            """;

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        PrintablesSearchResultsDto result = await client.SearchModelsAsync("bracket", 0, 20, null, CancellationToken.None);

        _ = result.Items.Should().HaveCount(1);
        _ = result.Items[0].Id.Should().Be("42");
        _ = result.Items[0].AuthorHandle.Should().Be("maker_jane");
    }

    [Fact]
    public async Task SearchModelsAsync_EmptyQuery_ThrowsArgumentException()
    {
        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, "{}"));
        Func<Task> act = () => client.SearchModelsAsync("   ", 0, 20, null, CancellationToken.None);

        _ = await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("query");
    }

    [Fact]
    public async Task SearchModelsAsync_ModelsPathFallback_ReturnsMappedPagedResult()
    {
        const string json = """
            {
              "data": {
                "searchPrints2": {
                  "totalCount": 1,
                  "items": [
                    {
                      "id": "88",
                      "name": "Fallback Model",
                      "description": "Legacy path",
                      "likesCount": 8,
                      "downloadCount": 16,
                      "user": { "handle": "fallback_user" }
                    }
                  ]
                }
              }
            }
            """;

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        PrintablesSearchResultsDto result = await client.SearchModelsAsync("fallback", 0, 20, null, CancellationToken.None);

        _ = result.Items.Should().HaveCount(1);
        _ = result.Items[0].Id.Should().Be("88");
        _ = result.Items[0].AuthorHandle.Should().Be("fallback_user");
        _ = result.Items[0].LikesCount.Should().Be(8);
        _ = result.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserCollectionsAsync_ReturnsMappedCollections()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "16", "handle": "maker_jane", "publicUsername": "Maker Jane", "avatarFilePath": "avatars/u1.png" }
                  ]
                }
              }
            }
            """,
            """
            {
              "data": {
                "userCollections": [
                  {
                    "id": "col-1",
                    "name": "Mechanical Parts",
                    "printsCount": 3,
                    "thumbnails": [
                      { "image": { "filePath": "media/col1.jpg" } }
                    ]
                  }
                ]
              }
            }
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));
        IReadOnlyList<PrintablesCollectionDto> result = await client.GetUserCollectionsAsync("maker_jane", CancellationToken.None);

        _ = result.Should().HaveCount(1);
        _ = result[0].Id.Should().Be("col-1");
        _ = result[0].Name.Should().Be("Mechanical Parts");
        _ = result[0].ModelCount.Should().Be(3);
        _ = result[0].ThumbnailUrl.Should().Be("https://media.printables.com/media/col1.jpg");
    }

    [Fact]
    public async Task GetUserModelsAsync_WithCursor_ReturnsHasNextPage()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "16", "handle": "maker_jane", "publicUsername": "Maker Jane" }
                  ]
                }
              }
            }
            """,
            """
            {
              "data": {
                "userModels": {
                  "cursor": "CURSOR-2",
                  "items": []
                }
              }
            }
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));
        PrintablesPagedResultDto<PrintablesModelCardDto> result = await client.GetUserModelsAsync("maker_jane", 25, null, "new_uploads", CancellationToken.None);

        _ = result.HasNextPage.Should().BeTrue();
        _ = result.NextCursor.Should().Be("CURSOR-2");
    }

    [Fact]
    public async Task GetUserModelsAsync_LimitCursorAndOrdering_AreNormalizedInRequestPayload()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "16", "handle": "maker_jane", "publicUsername": "Maker Jane" }
                  ]
                }
              }
            }
            """,
            """
            {
              "data": {
                "userModels": {
                  "cursor": null,
                  "items": []
                }
              }
            }
            """);
        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));

        _ = await client.GetUserModelsAsync(" maker_jane ", 250, "  CURSOR-1  ", "  downloads  ", CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(handler.RequestBodies.Last());
        JsonElement variables = payload.RootElement.GetProperty("variables");
        _ = variables.GetProperty("userId").GetString().Should().Be("16");
        _ = variables.GetProperty("limit").GetInt32().Should().Be(100);
        _ = variables.GetProperty("cursor").GetString().Should().Be("CURSOR-1");
        _ = variables.GetProperty("ordering").GetProperty("orderBy").GetString().Should().Be("downloads");
    }

    [Fact]
    public async Task GetPrintProfileAsync_TransientFailure_RetriesAndSucceeds()
    {
        TransientThenSuccessHandler handler = new(
            failCount: 1,
            successJson: """
                {
                  "data": {
                    "print": {
                      "id": "42",
                      "name": "Awesome Bracket",
                      "user": { "handle": "maker_jane" },
                      "license": { "name": "CC BY 4.0" },
                      "image": { "filePath": "https://media.printables.com/thumb.jpg" },
                      "stls": []
                    }
                  }
                }
                """);

        PrintablesGraphQLClient client = BuildClient(
            new HttpClient(handler),
            new PrintablesGraphQlOptions { MaxAttempts = 3, RetryBaseDelayMs = 1, CacheTtlSeconds = 0 });

        PrintablesPrintProfileDto result = await client.GetPrintProfileAsync("42", CancellationToken.None);

        _ = result.Id.Should().Be("42");
        _ = handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task FetchPreviewAsync_HttpRequestFailure_ThrowsPrintablesApiException()
    {
        PrintablesGraphQLClient client = BuildClient(BuildThrowingHttpClient(new HttpRequestException("network down")));
        Func<Task> act = () => client.FetchPreviewAsync("1", "https://www.printables.com/model/1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*Failed to reach Printables*");
    }

    [Fact]
    public async Task GetStlDownloadUrlAsync_HappyPath_ReturnsDownloadLink()
    {
        const string json = """
            {
              "data": {
                "getDownloadLink": {
                  "ok": true,
                  "errors": [],
                  "output": {
                    "link": "https://downloads.printables.com/files/example.stl"
                  }
                }
              }
            }
            """;

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        string link = await client.GetStlDownloadUrlAsync("42", "s1", default);

        _ = link.Should().Be("https://downloads.printables.com/files/example.stl");
    }

    [Fact]
    public async Task GetStlDownloadUrlAsync_DownloadRejected_ThrowsPrintablesApiException()
    {
        const string json = """
            {
              "data": {
                "getDownloadLink": {
                  "ok": false,
                  "errors": [
                    {
                      "field": "id",
                      "messages": ["File not available"]
                    }
                  ],
                  "output": null
                }
              }
            }
            """;

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.GetStlDownloadUrlAsync("42", "missing", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*File not available*");
    }

    [Fact]
    public async Task GetStlDownloadUrlAsync_MissingOutputLink_ThrowsPrintablesApiException()
    {
        const string json = """
            {
              "data": {
                "getDownloadLink": {
                  "ok": true,
                  "errors": [],
                  "output": {}
                }
              }
            }
            """;

        PrintablesGraphQLClient client = BuildClient(BuildMockedHttpClient(HttpStatusCode.OK, json));
        Func<Task> act = () => client.GetStlDownloadUrlAsync("42", "s1", default);

        _ = await act.Should().ThrowAsync<PrintablesApiException>()
            .WithMessage("*did not return a usable download link*");
    }

    // ── PrintablesImportService.ImportAsync ───────────────────────────────────

    [Fact]
    public async Task ImportAsync_NullFileIds_ImportsAllFiles()
    {
        PrintablesGraphQLClient client = BuildClient(new HttpClient(new PrintablesImportTestHttpMessageHandler()));
        Mock<IModel3DFileService> modelServiceMock = new(MockBehavior.Strict);
        List<string> uploadedFileNames = [];
        int uploadIndex = 0;

        _ = modelServiceMock
            .Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IFormFile file, CancellationToken _) =>
            {
                uploadedFileNames.Add(file.FileName);
                uploadIndex++;
                return new Model3DUploadResultDto
                {
                    Id = Guid.Parse($"00000000-0000-0000-0000-{uploadIndex:000000000000}"),
                    Name = file.FileName,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    FileType = Path.GetExtension(file.FileName).TrimStart('.'),
                    UploadedAt = DateTime.UtcNow,
                    Url = $"/api/3d-models/file/{uploadIndex}",
                };
            });
        _ = modelServiceMock
            .Setup(s => s.SetAttributionAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PrintablesImportService service = new(client, modelServiceMock.Object, Mock.Of<ILogger<PrintablesImportService>>());

        IReadOnlyList<Model3DUploadResultDto> result = await service.ImportAsync("https://www.printables.com/model/42-awesome-bracket", null, CancellationToken.None);

        _ = result.Should().HaveCount(2);
        _ = uploadedFileNames.Should().Equal("bracket_v1.stl", "bracket_v2.stl");
        modelServiceMock.Verify(s => s.SetAttributionAsync(It.IsAny<Guid>(), "https://www.printables.com/model/42-awesome-bracket", "maker_jane", "CC BY 4.0", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ImportAsync_SelectedFileIds_ImportsOnlyMatchingFiles()
    {
        PrintablesGraphQLClient client = BuildClient(new HttpClient(new PrintablesImportTestHttpMessageHandler()));
        Mock<IModel3DFileService> modelServiceMock = new(MockBehavior.Strict);
        List<string> uploadedFileNames = [];

        _ = modelServiceMock
            .Setup(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IFormFile file, CancellationToken _) =>
            {
                uploadedFileNames.Add(file.FileName);
                return new Model3DUploadResultDto
                {
                    Id = Guid.NewGuid(),
                    Name = file.FileName,
                    FileName = file.FileName,
                    FileSize = file.Length,
                    FileType = Path.GetExtension(file.FileName).TrimStart('.'),
                    UploadedAt = DateTime.UtcNow,
                    Url = "/api/3d-models/file/test",
                };
            });
        _ = modelServiceMock
            .Setup(s => s.SetAttributionAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        PrintablesImportService service = new(client, modelServiceMock.Object, Mock.Of<ILogger<PrintablesImportService>>());

        IReadOnlyList<Model3DUploadResultDto> result = await service.ImportAsync(
            "https://www.printables.com/model/42-awesome-bracket",
            ["s2"],
            CancellationToken.None);

        _ = result.Should().HaveCount(1);
        _ = uploadedFileNames.Should().Equal("bracket_v2.stl");
        modelServiceMock.Verify(s => s.SetAttributionAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportAsync_UnknownFileIds_ThrowsArgumentException()
    {
        PrintablesGraphQLClient client = BuildClient(new HttpClient(new PrintablesImportTestHttpMessageHandler()));
        Mock<IModel3DFileService> modelServiceMock = new(MockBehavior.Strict);
        PrintablesImportService service = new(client, modelServiceMock.Object, Mock.Of<ILogger<PrintablesImportService>>());

        Func<Task> act = () => service.ImportAsync(
            "https://www.printables.com/model/42-awesome-bracket",
            ["missing-file"],
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*missing-file*");
        modelServiceMock.Verify(s => s.UploadModelAsync(It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Controller: preview endpoint ──────────────────────────────────────────

    private static PrintablesImportController BuildController(Mock<IPrintablesImportService> mockSvc)
    {
        Mock<ILogger<PrintablesImportController>> logger = new();
        Mock<IPrintablesOAuthService> oauthSvc = new();
        return new PrintablesImportController(mockSvc.Object, oauthSvc.Object, logger.Object);
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

    [Fact]
    public async Task ImportAsync_HappyPath_Returns200WithUploadedModels()
    {
        IReadOnlyList<Model3DUploadResultDto> dto =
        [
            new Model3DUploadResultDto
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000042"),
                Name = "Awesome Bracket",
                FileName = "bracket.stl",
                FileSize = 1024,
                FileType = "stl",
                UploadedAt = DateTime.UtcNow,
                Url = "/api/3d-models/file/42",
            },
        ];

        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.ImportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.ImportAsync(
            new PrintablesImportRequest { Url = "https://www.printables.com/model/42-awesome-bracket", FileIds = ["s1"] },
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<Model3DUploadResultDto> returned = Assert.IsAssignableFrom<IReadOnlyList<Model3DUploadResultDto>>(ok.Value);
        _ = returned.Should().HaveCount(1);
        _ = returned[0].Id.Should().Be(Guid.Parse("00000000-0000-0000-0000-000000000042"));
    }

    [Fact]
    public async Task ImportAsync_EmptyUrl_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        PrintablesImportController controller = BuildController(svcMock);

        IActionResult result = await controller.ImportAsync(new PrintablesImportRequest { Url = string.Empty }, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImportAsync_InvalidUrl_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        PrintablesImportController controller = BuildController(svcMock);

        _ = svcMock
            .Setup(s => s.ImportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Not a Printables URL."));

        IActionResult result = await controller.ImportAsync(
            new PrintablesImportRequest { Url = "https://thingiverse.com/thing:1" },
            CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImportOneClickAsync_HappyPath_Returns200WithUploadedModels()
    {
        IReadOnlyList<Model3DUploadResultDto> dto =
        [
            new Model3DUploadResultDto
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000099"),
                Name = "One Click",
                FileName = "one-click.stl",
                FileSize = 2048,
                FileType = "stl",
                UploadedAt = DateTime.UtcNow,
                Url = "/api/3d-models/file/99",
            },
        ];

        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.ImportOneClickAsync(It.IsAny<PrintablesOneClickImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);

        Mock<IPrintablesOAuthService> oauthSvcMock = new();
        Mock<ILogger<PrintablesImportController>> logger = new();
        PrintablesImportController controller = new(svcMock.Object, oauthSvcMock.Object, logger.Object);

        IActionResult result = await controller.ImportOneClickAsync(
            new PrintablesOneClickImportRequest { ModelId = "99", Slug = "one-click" },
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<Model3DUploadResultDto> returned = Assert.IsAssignableFrom<IReadOnlyList<Model3DUploadResultDto>>(ok.Value);
        _ = returned.Should().HaveCount(1);
    }

    [Fact]
    public async Task ImportOneClickAsync_InvalidModelId_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.ImportOneClickAsync(It.IsAny<PrintablesOneClickImportRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("modelId must be numeric."));

        Mock<IPrintablesOAuthService> oauthSvcMock = new();
        Mock<ILogger<PrintablesImportController>> logger = new();
        PrintablesImportController controller = new(svcMock.Object, oauthSvcMock.Object, logger.Object);

        IActionResult result = await controller.ImportOneClickAsync(
            new PrintablesOneClickImportRequest { ModelId = "abc" },
            CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task BrowseCollectionsAsync_UserNotFound_Returns404()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.BrowseCollectionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("not found"));

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.BrowseCollectionsAsync("missing-user", CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task BrowseUserModelsAsync_InvalidLimit_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        PrintablesImportController controller = BuildController(svcMock);

        IActionResult result = await controller.BrowseUserModelsAsync("maker_jane", 0, null, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SearchModelsAsync_HappyPath_Returns200()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        _ = svcMock
            .Setup(s => s.SearchModelsAsync("benchy", 0, 24, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrintablesSearchResultsDto(
                Items: [new PrintablesModelSummaryDto("1", "Model", "model", "maker", null, null, 1, 2, "https://www.printables.com/model/1-model")],
                TotalCount: 1,
                Offset: 0,
                Limit: 24,
                HasMore: false));

        PrintablesImportController controller = BuildController(svcMock);
        IActionResult result = await controller.SearchModelsAsync("benchy", 0, 24, null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesSearchResultsDto payload = Assert.IsType<PrintablesSearchResultsDto>(ok.Value);
        _ = payload.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchModelsAsync_EmptyQuery_Returns400()
    {
        Mock<IPrintablesImportService> svcMock = new(MockBehavior.Strict);
        PrintablesImportController controller = BuildController(svcMock);

        IActionResult result = await controller.SearchModelsAsync("", 0, 24, null, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    private sealed class PrintablesImportTestHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                string? operationName = root.TryGetProperty("operationName", out JsonElement operationNameEl) && operationNameEl.ValueKind == JsonValueKind.String
                    ? operationNameEl.GetString()
                    : null;

                if (string.Equals(operationName, "GetDownloadLink", StringComparison.Ordinal))
                {
                    string fileId = root.GetProperty("variables").GetProperty("id").GetString()!;
                    string link = $"https://downloads.printables.com/{fileId}.stl";
                    return CreateJsonResponse($$"""
                    {
                      "data": {
                        "getDownloadLink": {
                          "ok": true,
                          "errors": [],
                          "output": {
                            "link": "{{link}}"
                          }
                        }
                      }
                    }
                    """);
                }

                return CreateJsonResponse("""
                {
                  "data": {
                    "print": {
                      "id": "42",
                      "name": "Awesome Bracket",
                      "user": { "handle": "maker_jane" },
                      "license": { "name": "CC BY 4.0" },
                      "image": { "filePath": "https://media.printables.com/thumb.jpg" },
                      "stls": [
                        { "id": "s1", "name": "bracket_v1.stl", "fileSize": 100 },
                        { "id": "s2", "name": "bracket_v2.stl", "fileSize": 200 }
                      ]
                    }
                  }
                }
                """);
            }

            if (request.Method == HttpMethod.Get)
            {
                string fileId = request.RequestUri!.Segments.Last().Replace(".stl", string.Empty, StringComparison.OrdinalIgnoreCase);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"solid {fileId}")),
                };
            }

            throw new InvalidOperationException($"Unhandled request: {request.Method} {request.RequestUri}");
        }

        private static HttpResponseMessage CreateJsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
    }

    private sealed class QueueingHttpMessageHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public int PostCount { get; private set; }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostCount++;
                string requestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                RequestBodies.Add(requestBody);
            }

            string json = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CountingHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostCount++;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TransientThenSuccessHandler(int failCount, string successJson) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount <= failCount)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RequestRecordingHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
