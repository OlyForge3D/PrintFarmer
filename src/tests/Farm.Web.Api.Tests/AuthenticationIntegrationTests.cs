using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class AuthenticationIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthenticationIntegrationTests(CustomWebApplicationFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Register_WithValidData_ShouldReturnSuccessAsync()
    {
        // Arrange
        var request = new RegisterRequest(
            "testuser",
            "test@example.com",
            "TestPassword123!",
            "Test",
            "User");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
        result.User.Should().NotBeNull();
        result.User!.Username.Should().Be("testuser");
        result.User.Email.Should().Be("test@example.com");
        result.User.Roles.Should().Contain("farm_user");
    }

    [Fact]
    public async Task Register_WithShortPassword_ShouldReturnBadRequestAsync()
    {
        // Arrange
        var request = new RegisterRequest(
            "testuser",
            "test@example.com",
            "123", // Too short
            "Test",
            "User");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().Contain("at least 6 characters");
    }

    [Fact]
    public async Task Register_WithDuplicateUsername_ShouldReturnBadRequestAsync()
    {
        // Arrange - Create first user
        var firstRequest = new RegisterRequest(
            "duplicateuser",
            "first@example.com",
            "TestPassword123!",
            "First",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", firstRequest);

        // Arrange - Try to create second user with same username
        var secondRequest = new RegisterRequest(
            "duplicateuser", // Same username
            "second@example.com",
            "TestPassword123!",
            "Second",
            "User");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/register", secondRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().Contain("already taken");
    }

    [Fact]
    public async Task Login_WithValidCredentials_ShouldReturnSuccessAsync()
    {
        // Arrange - Create a user first
        var registerRequest = new RegisterRequest(
            "loginuser",
            "login@example.com",
            "TestPassword123!",
            "Login",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        var loginRequest = new LoginRequest("loginuser", "TestPassword123!");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
        result.User.Should().NotBeNull();
        result.User!.Username.Should().Be("loginuser");
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_ShouldReturnUnauthorizedAsync()
    {
        // Arrange
        var loginRequest = new LoginRequest("nonexistentuser", "WrongPassword123!");

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithMissingFields_ShouldReturnBadRequestAsync()
    {
        // Arrange
        var loginRequest = new LoginRequest("", "TestPassword123!"); // Empty username

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().Contain("Username and password are required");
    }

    [Fact]
    public async Task GetCurrentUser_WithValidToken_ShouldReturnUserInfoAsync()
    {
        // Arrange - Create and login user
        var registerRequest = new RegisterRequest(
            "currentuser",
            "current@example.com",
            "TestPassword123!",
            "Current",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        var loginRequest = new LoginRequest("currentuser", "TestPassword123!");

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // Add JWT token to client
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Username.Should().Be("currentuser");
        user.Email.Should().Be("current@example.com");
        user.FirstName.Should().Be("Current");
        user.LastName.Should().Be("User");
        user.Roles.Should().Contain("farm_user");
    }

    [Fact]
    public async Task GetCurrentUser_WithoutToken_ShouldReturnUnauthorizedAsync()
    {
        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_WithValidData_ShouldReturnSuccessAsync()
    {
        // Arrange - Create and login user
        var registerRequest = new RegisterRequest(
            "passworduser",
            "password@example.com",
            "OldPassword123!",
            "Password",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        var loginRequest = new LoginRequest("passworduser", "OldPassword123!");

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // Add JWT token to client
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

        var changePasswordRequest = new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword123!",
            NewPassword = "NewPassword123!"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", changePasswordRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Password changed successfully");

        // Verify new password works by logging in
        var newLoginRequest = new LoginRequest("passworduser", "NewPassword123!"); // New password

        var newLoginResponse = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", newLoginRequest);
        newLoginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_ShouldReturnBadRequestAsync()
    {
        // Arrange - Create and login user
        var registerRequest = new RegisterRequest(
            "wrongpassuser",
            "wrongpass@example.com",
            "OldPassword123!",
            "Wrong",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        var loginRequest = new LoginRequest("wrongpassuser", "OldPassword123!");

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // Add JWT token to client
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

        var changePasswordRequest = new ChangePasswordRequest
        {
            CurrentPassword = "WrongPassword123!", // Wrong current password
            NewPassword = "NewPassword123!"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", changePasswordRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Current password is incorrect");
    }

    [Fact]
    public async Task Logout_WithValidToken_ShouldReturnSuccessAsync()
    {
        // Arrange - Create and login user
        var registerRequest = new RegisterRequest(
            "logoutuser",
            "logout@example.com",
            "TestPassword123!",
            "Logout",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        var loginRequest = new LoginRequest("logoutuser", "TestPassword123!");

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // Add JWT token to client
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

        // Act
        var response = await _client.PostAsync("/api/auth/logout", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Logged out successfully");
    }

    [Fact]
    public async Task Logout_WithoutToken_ShouldReturnUnauthorizedAsync()
    {
        // Act
        var response = await _client.PostAsync("/api/auth/logout", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
