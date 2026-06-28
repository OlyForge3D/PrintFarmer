using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
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
    public async Task ResolveUserProfileAsync_AtPrefixedUsername_NormalizesAndResolves()
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
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));
        PrintablesUserProfileDto result = await client.ResolveUserProfileAsync("@maker_jane", CancellationToken.None);

        _ = result.Id.Should().Be("16");
        _ = result.Handle.Should().Be("maker_jane");
    }

    [Fact]
    public async Task GetUserCollectionsAsync_AtPrefixedUsername_ResolvesAndReturnsCollections()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "573746", "handle": "JeffRho", "publicUsername": "JeffRho" }
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
                    "id": "col-77",
                    "name": "Public",
                    "printsCount": 4
                  }
                ]
              }
            }
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));
        IReadOnlyList<PrintablesCollectionDto> result = await client.GetUserCollectionsAsync("@JeffRho", CancellationToken.None);

        _ = result.Should().HaveCount(1);
        _ = result[0].Id.Should().Be("col-77");
        _ = result[0].Name.Should().Be("Public");
    }

    [Fact]
    public async Task GetUserCollectionsAsync_UrlEncodedAtUsername_ResolvesAndReturnsCollections()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "searchUsers2": {
                  "items": [
                    { "id": "573746", "handle": "JeffRho", "publicUsername": "JeffRho" }
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
                    "id": "col-88",
                    "name": "Encoded",
                    "printsCount": 2
                  }
                ]
              }
            }
            """);

        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));
        IReadOnlyList<PrintablesCollectionDto> result = await client.GetUserCollectionsAsync("%40JeffRho", CancellationToken.None);

        _ = result.Should().HaveCount(1);
        _ = result[0].Id.Should().Be("col-88");
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
    public async Task GetCollectionModelsAsync_OrderingMissing_UsesAddedToCollectionDefault()
    {
        QueueingHttpMessageHandler handler = new(
            """
            {
              "data": {
                "moreCollectionModels": {
                  "cursor": null,
                  "items": []
                }
              }
            }
            """);
        PrintablesGraphQLClient client = BuildClient(new HttpClient(handler));

        _ = await client.GetCollectionModelsAsync(" 2695539 ", 24, null, null, null, CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(handler.RequestBodies.Last());
        JsonElement variables = payload.RootElement.GetProperty("variables");
        _ = variables.GetProperty("collectionId").GetString().Should().Be("2695539");
        _ = variables.GetProperty("ordering").GetString().Should().Be("added_to_collection");
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

    [Fact]
    public async Task BrowseCollectionsAsync_AtPrefixedUsername_UsesResolvedHandleForCollections()
    {
        Mock<IPrintablesGraphQLClient> graphQlMock = new(MockBehavior.Strict);
        Mock<IModel3DFileService> modelServiceMock = new(MockBehavior.Strict);
        PrintablesUserProfileDto user = new(
            Id: "573746",
            Handle: "JeffRho",
            PublicUsername: "JeffRho",
            AvatarUrl: null);

        _ = graphQlMock
            .Setup(x => x.ResolveUserProfileAsync("@JeffRho", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _ = graphQlMock
            .Setup(x => x.GetUserCollectionsAsync("JeffRho", It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new PrintablesCollectionDto(
                    Id: "col-1",
                    Name: "Public",
                    Slug: null,
                    Description: null,
                    ModelCount: 1,
                    ThumbnailUrl: null),
            ]);

        PrintablesImportService service = new(graphQlMock.Object, modelServiceMock.Object, Mock.Of<ILogger<PrintablesImportService>>());
        PrintablesCollectionsBrowseDto result = await service.BrowseCollectionsAsync("@JeffRho", CancellationToken.None);

        _ = result.User.Handle.Should().Be("JeffRho");
        _ = result.Collections.Should().HaveCount(1);
        graphQlMock.Verify(x => x.GetUserCollectionsAsync("JeffRho", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Controller: preview endpoint ──────────────────────────────────────────

    private static PrintablesImportController BuildController(Mock<IPrintablesImportService> mockSvc)
    {
        Mock<ILogger<PrintablesImportController>> logger = new();
        Mock<IPrintablesOAuthService> oauthSvc = new();
        return BuildController(mockSvc, oauthSvc);
    }

    private static PrintablesImportController BuildController(
        Mock<IPrintablesImportService> importService,
        Mock<IPrintablesOAuthService> oauthService,
        Guid? userId = null)
    {
        Mock<ILogger<PrintablesImportController>> logger = new();
        PrintablesImportController controller = new(importService.Object, oauthService.Object, logger.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", (userId ?? Guid.Parse("71AE89EB-EAE9-41EA-8146-17F6C689E5F7")).ToString()),
                    ], "TestAuth")),
                },
            },
        };

        return controller;
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

    [Fact]
    public async Task OAuthStatusAsync_Linked_ReturnsStatusPayload()
    {
        Guid userId = Guid.Parse("D17FC7AD-2DB7-4A6D-8996-05935EDAA8D8");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        PrintablesOAuthStatusDto status = new(
            IsLinked: true,
            AccessTokenExpiresAtUtc: DateTime.UtcNow.AddHours(1),
            LinkedAtUtc: DateTime.UtcNow.AddMinutes(-10),
            HasRefreshToken: true,
            Scope: "read");

        _ = oauthSvcMock
            .Setup(s => s.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthStatusAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesOAuthStatusDto payload = Assert.IsType<PrintablesOAuthStatusDto>(ok.Value);
        _ = payload.IsLinked.Should().BeTrue();
        _ = payload.HasRefreshToken.Should().BeTrue();
    }

    [Fact]
    public async Task OAuthStatusAsync_Unlinked_ReturnsStatusPayload()
    {
        Guid userId = Guid.Parse("635AB186-8FAA-45AF-B423-B754E6EF2E56");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        PrintablesOAuthStatusDto status = new(
            IsLinked: false,
            AccessTokenExpiresAtUtc: null,
            LinkedAtUtc: null,
            HasRefreshToken: false,
            Scope: null);

        _ = oauthSvcMock
            .Setup(s => s.GetStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthStatusAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesOAuthStatusDto payload = Assert.IsType<PrintablesOAuthStatusDto>(ok.Value);
        _ = payload.IsLinked.Should().BeFalse();
        _ = payload.AccessTokenExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task OAuthCallbackAsync_ExpiredState_Returns400()
    {
        Guid userId = Guid.Parse("459B9D82-0249-4E0F-A75B-9D2D5A674D6A");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.HandleCallbackAsync(userId, "oauth-code", "expired-state", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("OAuth state is invalid or expired.", "state"));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthCallbackAsync("oauth-code", "expired-state", CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        _ = badRequest.Value.Should().BeOfType<string>().Which.Should().Contain("OAuth state is invalid or expired.");
    }

    [Fact]
    public async Task OAuthCallbackAsync_TokenExchangeError_Returns502()
    {
        Guid userId = Guid.Parse("DBE2D9D5-A2BF-43B7-810C-4BC41E12BC35");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.HandleCallbackAsync(userId, "oauth-code", "state-ok", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesApiException("Printables OAuth token exchange failed with HTTP 503."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthCallbackAsync("oauth-code", "state-ok", CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task OAuthCallbackAsync_NotLinkedConflict_Returns409()
    {
        Guid userId = Guid.Parse("E7736E84-78CF-4D66-B783-73008780A6D2");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.HandleCallbackAsync(userId, "oauth-code", "state-ok", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthNotLinkedException("Printables account is not linked."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthCallbackAsync("oauth-code", "state-ok", CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        _ = conflict.Value.Should().Be("Printables account is not linked.");
    }

    [Fact]
    public async Task OAuthCallbackAsync_TransientUnavailable_Returns503()
    {
        Guid userId = Guid.Parse("A11F7B78-CC84-4A37-8861-33B964624B1A");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.HandleCallbackAsync(userId, "oauth-code", "state-ok", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthTemporarilyUnavailableException("temporary outage"));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthCallbackAsync("oauth-code", "state-ok", CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task OAuthDisconnectAsync_TransientUnavailable_Returns503()
    {
        Guid userId = Guid.Parse("F0D6179B-E8DF-4F9A-88FB-10CC4CA3B5C9");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.DisconnectAsync(userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthTemporarilyUnavailableException("temporary outage"));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.OAuthDisconnectAsync(CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GetLikedModelsAsync_ReturnsPageForLinkedUser()
    {
        Guid userId = Guid.Parse("0359F793-4A27-46CC-B711-89C61A76A2CF");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        PrintablesAuthenticatedCursorPageDto page = new(
            Items:
            [
                new PrintablesModelSummaryDto(
                    Id: "liked-1",
                    Name: "Voron Clip",
                    Slug: "voron-clip",
                    AuthorHandle: "ripley",
                    AuthorName: null,
                    ThumbnailUrl: null,
                    LikesCount: 9,
                    DownloadCount: 14,
                    SourceUrl: "https://www.printables.com/model/liked-1-voron-clip"),
            ],
            NextCursor: "cursor-2",
            HasMore: true);

        _ = oauthSvcMock
            .Setup(s => s.GetLikedModelsAsync(userId, 24, "cursor-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetLikedModelsAsync(24, "cursor-1", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesAuthenticatedCursorPageDto payload = Assert.IsType<PrintablesAuthenticatedCursorPageDto>(ok.Value);
        _ = payload.Items.Should().HaveCount(1);
        _ = payload.NextCursor.Should().Be("cursor-2");
    }

    [Fact]
    public async Task GetLikedModelsAsync_UnlinkedUser_Returns409()
    {
        Guid userId = Guid.Parse("93B2BF50-1D90-4FCF-BD70-8437D8B5E73B");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetLikedModelsAsync(userId, 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthNotLinkedException("Printables account is not linked."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetLikedModelsAsync(24, null, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        _ = conflict.Value.Should().Be("Printables account is not linked.");
    }

    [Fact]
    public async Task GetLikedModelsAsync_ServiceError_Returns501()
    {
        Guid userId = Guid.Parse("5D44C478-E6C3-4A67-B25B-F856B49AD687");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetLikedModelsAsync(userId, 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("TODO: Implement Printables liked models query mapping against authenticated API."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetLikedModelsAsync(24, null, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task GetLikedModelsAsync_TransientUpstream_Returns503()
    {
        Guid userId = Guid.Parse("A8D76A58-8CC4-4D00-9502-1C2A4E4B5F3F");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetLikedModelsAsync(userId, 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthTemporarilyUnavailableException("temporary outage"));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetLikedModelsAsync(24, null, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_ReturnsPageForLinkedUser()
    {
        Guid userId = Guid.Parse("7E109FCE-2A98-43C1-8BEE-490F62B1B3DC");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        PrintablesAuthenticatedCursorPageDto page = new(
            Items:
            [
                new PrintablesModelSummaryDto(
                    Id: "history-1",
                    Name: "History Model",
                    Slug: "history-model",
                    AuthorHandle: "ripley",
                    AuthorName: null,
                    ThumbnailUrl: null,
                    LikesCount: 2,
                    DownloadCount: 8,
                    SourceUrl: "https://www.printables.com/model/history-1-history-model"),
            ],
            NextCursor: null,
            HasMore: false);

        _ = oauthSvcMock
            .Setup(s => s.GetDownloadHistoryAsync(userId, 12, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetDownloadHistoryAsync(12, null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        PrintablesAuthenticatedCursorPageDto payload = Assert.IsType<PrintablesAuthenticatedCursorPageDto>(ok.Value);
        _ = payload.HasMore.Should().BeFalse();
        _ = payload.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_UnlinkedUser_Returns409()
    {
        Guid userId = Guid.Parse("65AA8992-03B2-49E0-A326-1028D7F247A8");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetDownloadHistoryAsync(userId, 24, "cursor-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthNotLinkedException("Printables account is not linked."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetDownloadHistoryAsync(24, "cursor-1", CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result);
        _ = conflict.Value.Should().Be("Printables account is not linked.");
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_ServiceError_Returns501()
    {
        Guid userId = Guid.Parse("1CC10B8E-C0CA-4D83-87B4-6F4A8FDE0185");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetDownloadHistoryAsync(userId, 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("TODO: Implement Printables download history query mapping against authenticated API."));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetDownloadHistoryAsync(24, null, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_TransientUpstream_Returns503()
    {
        Guid userId = Guid.Parse("1D6D79C8-A18A-4A35-A724-7B121B93F55C");
        Mock<IPrintablesImportService> importSvcMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthSvcMock = new(MockBehavior.Strict);
        _ = oauthSvcMock
            .Setup(s => s.GetDownloadHistoryAsync(userId, 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PrintablesOAuthTemporarilyUnavailableException("temporary outage"));

        PrintablesImportController controller = BuildController(importSvcMock, oauthSvcMock, userId);
        IActionResult result = await controller.GetDownloadHistoryAsync(24, null, CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        _ = objectResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
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
