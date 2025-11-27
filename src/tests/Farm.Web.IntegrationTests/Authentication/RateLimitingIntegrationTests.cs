using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Farm.Web.IntegrationTests;
using Farm.Web.Shared.Contracts.Auth;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Authentication;

[Collection("Sequential")]
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
        HttpClient client = CreateClient();
        LoginRequest loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "TestPassword123!"
        };

        // Act - Make 3 login attempts (well within the 10/minute limit)
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.1");
        HttpResponseMessage response1 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        HttpResponseMessage response2 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        HttpResponseMessage response3 = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - All should process (may fail auth, but not rate limited)
        _ = response1.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        _ = response2.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        _ = response3.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Login_ExceedingRateLimit_ShouldReturn429()
    {
        // Arrange
        HttpClient client = CreateClient();
        LoginRequest loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Make 11 rapid login attempts (exceeds 10/minute limit)
        List<HttpResponseMessage> responses = new List<HttpResponseMessage>();
        _ = client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.2");
        for (int i = 0; i < 11; i++)
        {
            responses.Add(await client.PostAsJsonAsync("/api/auth/login", loginRequest));
        }

        // Assert - At least one should be rate limited
        List<HttpResponseMessage> rateLimitedResponses = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        _ = rateLimitedResponses.Should().NotBeEmpty("at least one request should be rate limited");

        // Check the rate limited response
        HttpResponseMessage rateLimitedResponse = rateLimitedResponses.First();
        _ = rateLimitedResponse.Headers.Should().ContainKey("Retry-After");

        RateLimitErrorResponse? content = await rateLimitedResponse.Content.ReadFromJsonAsync<RateLimitErrorResponse>();
        _ = content.Should().NotBeNull();
        _ = content!.Error.Should().Be("Too Many Requests");
        _ = content.Message.Should().Contain("login");
        _ = content.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Register_ExceedingRateLimit_ShouldReturn429()
    {
        // Arrange
        HttpClient client = CreateClient();

        // Act - Make 11 rapid registration attempts (exceeds 10/minute limit)
        List<HttpResponseMessage> responses = new List<HttpResponseMessage>();
        _ = client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.3");
        for (int i = 0; i < 11; i++)
        {
            RegisterRequest registerRequest = new RegisterRequest
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
        List<HttpResponseMessage> rateLimitedResponses = responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests).ToList();
        _ = rateLimitedResponses.Should().NotBeEmpty("at least one request should be rate limited");

        // Check the rate limited response
        HttpResponseMessage rateLimitedResponse = rateLimitedResponses.First();
        _ = rateLimitedResponse.Headers.Should().ContainKey("Retry-After");

        RateLimitErrorResponse? content = await rateLimitedResponse.Content.ReadFromJsonAsync<RateLimitErrorResponse>();
        _ = content.Should().NotBeNull();
        _ = content!.Error.Should().Be("Too Many Requests");
        _ = content.Message.Should().Contain("register");
    }

    [Fact]
    public async Task Login_RateLimitDoesNotAffectOtherEndpoints_ShouldSucceed()
    {
        // Arrange
        HttpClient client = CreateClient();
        LoginRequest loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Exhaust login rate limit
        for (int i = 0; i < 11; i++)
        {
            _ = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // Try to access a different endpoint (health check)
        HttpResponseMessage healthResponse = await client.GetAsync("/health");

        // Assert - Health endpoint should still work
        _ = healthResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        _ = healthResponse.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Register_RateLimitIsIndependentFromLogin_ShouldSucceed()
    {
        // Arrange
        HttpClient client = CreateClient();
        LoginRequest loginRequest = new LoginRequest
        {
            UsernameOrEmail = "testuser",
            Password = "password"
        };

        // Act - Exhaust login rate limit
        for (int i = 0; i < 11; i++)
        {
            _ = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // Try to register (different rate limit counter)
        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = "newuser",
            Email = "newuser@example.com",
            Password = "Password123!",
            ConfirmPassword = "Password123!",
            FirstName = "New",
            LastName = "User"
        };
        _ = client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.4");
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerRequest);

        // Assert - Register should not be rate limited (independent counter)
        _ = registerResponse.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    private record RateLimitErrorResponse(
        string Error,
        string Message,
        double RetryAfterSeconds);
}
