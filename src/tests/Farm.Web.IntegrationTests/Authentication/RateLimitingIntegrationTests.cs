using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Farm.Web.IntegrationTests;
using Farm.Web.Shared.Contracts.Auth;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Authentication;

public class RateLimitingIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RateLimitingIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task Login_WithinRateLimit_ShouldSucceed()
    {
        // Arrange
        var client = CreateClient();
        var loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "TestPassword123!"
        };

        // Act - Make 3 login attempts (well within the 10/minute limit)
        var response1 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var response2 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var response3 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - All should process (may fail auth, but not rate limited)
        response1.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        response2.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        response3.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Login_ExceedingRateLimit_ShouldReturn429()
    {
        // Arrange
        var client = CreateClient();
        var loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Make 11 rapid login attempts (exceeds 10/minute limit)
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 11; i++)
        {
            responses.Add(await client.PostAsJsonAsync("/api/auth/login", loginRequest));
        }

        // Assert - At least one should be rate limited
        var rateLimitedResponses = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        rateLimitedResponses.Should().NotBeEmpty("at least one request should be rate limited");

        // Check the rate limited response
        var rateLimitedResponse = rateLimitedResponses.First();
        rateLimitedResponse.Headers.Should().ContainKey("Retry-After");

        var content = await rateLimitedResponse.Content.ReadFromJsonAsync<RateLimitErrorResponse>();
        content.Should().NotBeNull();
        content!.Error.Should().Be("Too Many Requests");
        content.Message.Should().Contain("login");
        content.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Register_ExceedingRateLimit_ShouldReturn429()
    {
        // Arrange
        var client = CreateClient();

        // Act - Make 11 rapid registration attempts (exceeds 10/minute limit)
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 11; i++)
        {
            var registerRequest = new RegisterRequest
            {
                Username = $"user{i}",
                Email = $"user{i}@example.com",
                Password = "Password123!",
                ConfirmPassword = "Password123!",
                FirstName = "Test",
                LastName = "User"
            };
            responses.Add(await client.PostAsJsonAsync("/api/auth/register", registerRequest));
        }

        // Assert - At least one should be rate limited
        var rateLimitedResponses = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        rateLimitedResponses.Should().NotBeEmpty("at least one request should be rate limited");

        // Check the rate limited response
        var rateLimitedResponse = rateLimitedResponses.First();
        rateLimitedResponse.Headers.Should().ContainKey("Retry-After");

        var content = await rateLimitedResponse.Content.ReadFromJsonAsync<RateLimitErrorResponse>();
        content.Should().NotBeNull();
        content!.Error.Should().Be("Too Many Requests");
        content.Message.Should().Contain("register");
    }

    [Fact]
    public async Task Login_RateLimitDoesNotAffectOtherEndpoints_ShouldSucceed()
    {
        // Arrange
        var client = CreateClient();
        var loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Exhaust login rate limit
        for (int i = 0; i < 11; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // Try to access a different endpoint (health check)
        var healthResponse = await client.GetAsync("/health");

        // Assert - Health endpoint should still work
        healthResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        healthResponse.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Register_RateLimitIsIndependentFromLogin_ShouldSucceed()
    {
        // Arrange
        var client = CreateClient();
        var loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Exhaust login rate limit
        for (int i = 0; i < 11; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // Try to register (different rate limit counter)
        var registerRequest = new RegisterRequest
        {
            Username = "newuser",
            Email = "newuser@example.com",
            Password = "Password123!",
            ConfirmPassword = "Password123!",
            FirstName = "New",
            LastName = "User"
        };
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerRequest);

        // Assert - Register should not be rate limited (independent counter)
        registerResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    private record RateLimitErrorResponse(
        string Error,
        string Message,
        double RetryAfterSeconds);
}
