using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Security;
using Farm.Slicer.Module.Api.Controllers;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class PrintablesOAuthServiceTests
{
    [Fact]
    public async Task GetLikedModelsAsync_Success_MapsResponse()
    {
        Guid userId = Guid.NewGuid();
        await using AppDbContext db = CreateDbContext();
        _ = db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            PrintablesOAuthAccessToken = "enc:access-token",
            PrintablesOAuthRefreshToken = "enc:refresh-token",
            PrintablesOAuthTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        });
        _ = await db.SaveChangesAsync();

        HttpClient client = new(new SequenceHttpHandler(
            req =>
            {
                req.Headers.Authorization.Should().NotBeNull();
                req.Headers.Authorization!.Scheme.Should().Be("Bearer");
                req.Headers.Authorization.Parameter.Should().Be("access-token");
                return Json(HttpStatusCode.OK, """
                    {
                      "data": {
                        "viewer": {
                          "likedModels": {
                            "cursor": "next-liked",
                            "items": [
                              {
                                "id": "42",
                                "name": "Bracket",
                                "slug": "bracket",
                                "likesCount": 12,
                                "downloadCount": 34,
                                "image": { "filePath": "thumbs/bracket.jpg" },
                                "user": { "handle": "maker_jane" }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
            }));

        PrintablesOAuthService sut = CreateService(db, client);

        PrintablesAuthenticatedCursorPageDto result = await sut.GetLikedModelsAsync(userId, 24, null, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.NextCursor.Should().Be("next-liked");
        result.HasMore.Should().BeTrue();
        result.Items[0].Id.Should().Be("42");
        result.Items[0].Name.Should().Be("Bracket");
        result.Items[0].AuthorHandle.Should().Be("maker_jane");
        result.Items[0].ThumbnailUrl.Should().Be("https://media.printables.com/thumbs/bracket.jpg");
        result.Items[0].SourceUrl.Should().Be("https://www.printables.com/model/42-bracket");
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_UpstreamDataUnavailable_ThrowsNotSupported()
    {
        Guid userId = Guid.NewGuid();
        await using AppDbContext db = CreateDbContext();
        _ = db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            PrintablesOAuthAccessToken = "enc:access-token",
            PrintablesOAuthRefreshToken = "enc:refresh-token",
            PrintablesOAuthTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        });
        _ = await db.SaveChangesAsync();

        HttpClient client = new(new SequenceHttpHandler(_ => Json(HttpStatusCode.OK, """{ "data": { "viewer": {} } }""")));
        PrintablesOAuthService sut = CreateService(db, client);

        Func<Task> act = () => sut.GetDownloadHistoryAsync(userId, 24, null, CancellationToken.None);
        _ = await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*unavailable from upstream*");
    }

    [Fact]
    public async Task GetLikedModelsAsync_NotLinked_ThrowsInvalidOperation()
    {
        await using AppDbContext db = CreateDbContext();
        PrintablesOAuthService sut = CreateService(db, new HttpClient(new SequenceHttpHandler(_ => Json(HttpStatusCode.OK, "{}"))));

        Func<Task> act = () => sut.GetLikedModelsAsync(Guid.NewGuid(), 24, null, CancellationToken.None);
        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not linked*");
    }

    [Fact]
    public async Task GetDownloadHistoryAsync_Unauthorized_RevokesStoredTokens()
    {
        Guid userId = Guid.NewGuid();
        await using AppDbContext db = CreateDbContext();
        _ = db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            PrintablesOAuthAccessToken = "enc:access-token",
            PrintablesOAuthRefreshToken = "enc:refresh-token",
            PrintablesOAuthTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        });
        _ = await db.SaveChangesAsync();

        HttpClient client = new(new SequenceHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        PrintablesOAuthService sut = CreateService(db, client);

        Func<Task> act = () => sut.GetDownloadHistoryAsync(userId, 24, null, CancellationToken.None);
        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Reconnect*");

        UserSettings settings = await db.UserSettings.SingleAsync(x => x.UserId == userId);
        settings.PrintablesOAuthAccessToken.Should().BeNull();
        settings.PrintablesOAuthRefreshToken.Should().BeNull();
        settings.PrintablesOAuthTokenExpiresAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetLikedModelsAsync_ExpiredToken_RefreshesThenQueries()
    {
        Guid userId = Guid.NewGuid();
        await using AppDbContext db = CreateDbContext();
        _ = db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            PrintablesOAuthAccessToken = "enc:expired-access",
            PrintablesOAuthRefreshToken = "enc:refresh-token",
            PrintablesOAuthTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-2),
        });
        _ = await db.SaveChangesAsync();

        string? bearerOnGraphQl = null;
        HttpClient client = new(new SequenceHttpHandler(
            req =>
            {
                req.RequestUri!.AbsoluteUri.Should().Contain("/oauth2/token");
                return Json(HttpStatusCode.OK, """
                    {
                      "access_token": "new-access",
                      "refresh_token": "new-refresh",
                      "token_type": "Bearer",
                      "scope": "likes history",
                      "expires_in": 3600
                    }
                    """);
            },
            req =>
            {
                bearerOnGraphQl = req.Headers.Authorization?.Parameter;
                return Json(HttpStatusCode.OK, """
                    {
                      "data": {
                        "viewer": {
                          "likedModels": {
                            "cursor": null,
                            "items": []
                          }
                        }
                      }
                    }
                    """);
            }));

        PrintablesOAuthService sut = CreateService(db, client);
        PrintablesAuthenticatedCursorPageDto page = await sut.GetLikedModelsAsync(userId, 24, null, CancellationToken.None);

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        bearerOnGraphQl.Should().Be("new-access");

        UserSettings settings = await db.UserSettings.SingleAsync(x => x.UserId == userId);
        settings.PrintablesOAuthAccessToken.Should().Be("enc:new-access");
        settings.PrintablesOAuthRefreshToken.Should().Be("enc:new-refresh");
        settings.PrintablesOAuthTokenExpiresAtUtc.Should().NotBeNull();
        settings.PrintablesOAuthTokenExpiresAtUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(30));
    }

    [Fact]
    public async Task Controller_Liked_WhenNotLinked_ReturnsConflict()
    {
        Mock<IPrintablesImportService> importMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthMock = new(MockBehavior.Strict);
        _ = oauthMock
            .Setup(x => x.GetLikedModelsAsync(It.IsAny<Guid>(), 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Printables account is not linked."));

        PrintablesImportController controller = new(importMock.Object, oauthMock.Object, Mock.Of<ILogger<PrintablesImportController>>());
        controller.ControllerContext = BuildControllerContext(Guid.NewGuid());

        IActionResult result = await controller.GetLikedModelsAsync(limit: 24, cursor: null, CancellationToken.None);
        _ = Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Controller_History_WhenUpstreamUnavailable_ReturnsNotImplemented()
    {
        Mock<IPrintablesImportService> importMock = new(MockBehavior.Strict);
        Mock<IPrintablesOAuthService> oauthMock = new(MockBehavior.Strict);
        _ = oauthMock
            .Setup(x => x.GetDownloadHistoryAsync(It.IsAny<Guid>(), 24, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("unavailable"));

        PrintablesImportController controller = new(importMock.Object, oauthMock.Object, Mock.Of<ILogger<PrintablesImportController>>());
        controller.ControllerContext = BuildControllerContext(Guid.NewGuid());

        IActionResult result = await controller.GetDownloadHistoryAsync(limit: 24, cursor: null, CancellationToken.None);
        ObjectResult status = Assert.IsType<ObjectResult>(result);
        status.StatusCode.Should().Be(StatusCodes.Status501NotImplemented);
    }

    private static ControllerContext BuildControllerContext(Guid userId)
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim("sub", userId.ToString()),
        ], "test"));

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = principal,
            },
        };
    }

    private static PrintablesOAuthService CreateService(AppDbContext db, HttpClient httpClient)
    {
        Mock<ISensitiveDataProtector> protector = new();
        _ = protector.Setup(x => x.Protect(It.IsAny<string?>()))
            .Returns<string?>(value => value is null ? null : $"enc:{value}");
        _ = protector.Setup(x => x.Unprotect(It.IsAny<string?>()))
            .Returns<string?>(value =>
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.StartsWith("enc:", StringComparison.Ordinal) ? value[4..] : null;
            });

        return new PrintablesOAuthService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            httpClient,
            protector.Object,
            Options.Create(new PrintablesOAuthOptions
            {
                ClientId = "client-id",
                ClientSecret = "client-secret",
                RedirectUri = "https://localhost/callback",
                EnableAuthenticatedQueries = true,
            }),
            Mock.Of<ILogger<PrintablesOAuthService>>());
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        AppDbContext db = new(options);
        _ = db.Database.EnsureCreated();
        return db;
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class SequenceHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new(responders);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (_responders.Count == 0)
            {
                throw new InvalidOperationException("No more queued HTTP responses.");
            }

            HttpResponseMessage response = _responders.Dequeue()(request);
            return Task.FromResult(response);
        }
    }
}
