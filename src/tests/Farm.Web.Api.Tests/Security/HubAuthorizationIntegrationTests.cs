using System.Net;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class HubAuthorizationIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("/hubs/printers/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/harvest/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/maintenance/negotiate?negotiateVersion=1")]
    public async Task NegotiateAsync_WithoutAuthentication_IsDenied(string route)
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(route, content: null);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PrinterHub_ProductionJwtQueryToken_AuthenticatesHandshake()
    {
        (_, string token) = await CreateUserAndTokenAsync();
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/hubs/printers/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrinterHub_JwtClient_CannotJoinUnauthorizedQueueOrProjectGroups()
    {
        (_, string token) = await CreateUserAndTokenAsync();
        await using HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress, "/hubs/printers"),
                options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                    options.HttpMessageHandlerFactory =
                        _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        string? accessToken =
                            await context.Options.AccessTokenProvider!();
                        accessToken.Should().NotBeNullOrWhiteSpace();
                        string authenticatedUrl = QueryHelpers.AddQueryString(
                            context.Uri.ToString(),
                            "access_token",
                            accessToken!);
                        return await _factory.Server
                            .CreateWebSocketClient()
                            .ConnectAsync(
                                new Uri(authenticatedUrl),
                                cancellationToken);
                    };
                })
            .Build();
        await connection.StartAsync();

        Func<Task> queueJoin = () => connection.InvokeAsync(
            "SubscribeToQueueJobAsync",
            Guid.NewGuid().ToString());
        Func<Task> projectJoin = () => connection.InvokeAsync(
            "SubscribeToProjectAsync",
            Guid.NewGuid().ToString());

        (await queueJoin.Should().ThrowAsync<HubException>())
            .Which.Message.Should().Contain("resource_forbidden");
        (await projectJoin.Should().ThrowAsync<HubException>())
            .Which.Message.Should().Contain("resource_forbidden");
    }

    private async Task<(Guid UserId, string Token)> CreateUserAndTokenAsync()
    {
        string username = $"hub-user-{Guid.NewGuid():N}";
        string email = $"{username}@example.test";
        const string password = "HubPassword123!";
        Guid userId = Guid.NewGuid();
        await using (AsyncServiceScope scope =
                     _factory.Services.CreateAsyncScope())
        {
            AppDbContext db =
                scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher =
                scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();
            db.Users.Add(new User
            {
                Id = userId,
                Username = username,
                Email = email,
                PasswordHash = passwordHasher.HashPassword(password),
                FirstName = "Hub",
                LastName = "User",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope =
                     _factory.Services.CreateAsyncScope())
        {
            IAuthenticationService auth =
                scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result =
                await auth.AuthenticateAsync(username, password);
            result.Success.Should().BeTrue();
            result.Token.Should().NotBeNullOrWhiteSpace();
            return (userId, result.Token!);
        }
    }
}
