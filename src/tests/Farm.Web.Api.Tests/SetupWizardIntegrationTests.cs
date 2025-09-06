using System.Net;
using System.Net.Http.Json;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

public class SetupWizardIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SetupWizardIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetSetupStatus_WhenNoAdminUsers_ShouldReturnNeedsSetup()
    {
        // Act
        var response = await _client.GetAsync("/api/setup/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("\"needsSetup\":true");
    }

    [Fact]
    public async Task CreateInitialAdmin_WithValidData_ShouldReturnSuccess()
    {
        // Arrange
        var request = new
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "AdminPassword123!",
            FirstName = "System",
            LastName = "Administrator"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Token.Should().NotBeNullOrEmpty();
        result.User.Should().NotBeNull();
        result.User!.Username.Should().Be("admin");
        result.User.Email.Should().Be("admin@example.com");
        result.User.Roles.Should().Contain("farm_admin");
        result.User.EmailConfirmed.Should().BeTrue(); // Admin should be auto-confirmed
    }

    [Fact]
    public async Task CreateInitialAdmin_WithShortPassword_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "short", // Too short for admin
            FirstName = "System",
            LastName = "Administrator"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("at least 8 characters");
    }

    [Fact]
    public async Task CreateInitialAdmin_WithMissingFields_ShouldReturnBadRequest()
    {
        // Arrange
        var request = new
        {
            Username = "", // Missing username
            Email = "admin@example.com",
            Password = "AdminPassword123!",
            FirstName = "System",
            LastName = "Administrator"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Username, email, and password are required");
    }

    [Fact]
    public async Task CreateInitialAdmin_WhenAdminAlreadyExists_ShouldReturnBadRequest()
    {
        // Arrange - First create an admin
        var firstRequest = new
        {
            Username = "firstadmin",
            Email = "firstadmin@example.com",
            Password = "AdminPassword123!",
            FirstName = "First",
            LastName = "Admin"
        };
        
        await _client.PostAsJsonAsync("/api/setup/initial-admin", firstRequest);

        // Arrange - Try to create another admin
        var secondRequest = new
        {
            Username = "secondadmin",
            Email = "secondadmin@example.com",
            Password = "AdminPassword123!",
            FirstName = "Second",
            LastName = "Admin"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", secondRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Setup has already been completed");
    }

    [Fact]
    public async Task GetSetupStatus_WhenAdminExists_ShouldReturnNoSetupNeeded()
    {
        // Arrange - Create an admin first
        var adminRequest = new
        {
            Username = "statusadmin",
            Email = "statusadmin@example.com",
            Password = "AdminPassword123!",
            FirstName = "Status",
            LastName = "Admin"
        };
        
        await _client.PostAsJsonAsync("/api/setup/initial-admin", adminRequest);

        // Act
        var response = await _factory.CreateClient().GetAsync("/api/setup/status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("\"needsSetup\":false");
    }

    [Fact]
    public async Task GetConfigurationOptions_ShouldReturnAvailableOptions()
    {
        // Act
        var response = await _client.GetAsync("/api/setup/config-options");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("DatabaseProviders");
        responseContent.Should().Contain("SQLite");
        responseContent.Should().Contain("SQL Server");
        responseContent.Should().Contain("PostgreSQL");
        responseContent.Should().Contain("MySQL");
        responseContent.Should().Contain("DefaultNetworkRanges");
        responseContent.Should().Contain("RecommendedPorts");
        responseContent.Should().Contain("Moonraker");
        responseContent.Should().Contain("PrusaLink");
    }

    [Fact]
    public async Task CreateInitialAdmin_WithDuplicateEmail_ShouldReturnBadRequest()
    {
        // Arrange - Create a regular user first with the same email
        var userRequest = new RegisterRequest(
            "regularuser",
            "duplicate@example.com",
            "TestPassword123!",
            "Regular",
            "User");
        
        await _client.PostAsJsonAsync("/api/auth/register", userRequest);

        // Arrange - Try to create admin with same email
        var adminRequest = new
        {
            Username = "admin",
            Email = "duplicate@example.com", // Same email
            Password = "AdminPassword123!",
            FirstName = "System",
            LastName = "Administrator"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", adminRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("already exists");
    }

    [Fact]
    public async Task InitialAdminLogin_AfterSetup_ShouldHaveAdminRole()
    {
        // Arrange - Create initial admin
        var adminRequest = new
        {
            Username = "adminlogin",
            Email = "adminlogin@example.com",
            Password = "AdminPassword123!",
            FirstName = "Admin",
            LastName = "Login"
        };
        
        var setupResponse = await _client.PostAsJsonAsync("/api/setup/initial-admin", adminRequest);
        var setupResult = await setupResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // Act - Login with the created admin
        var loginRequest = new LoginRequest("adminlogin", "AdminPassword123!");

        var loginResponse = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
        loginResult.Should().NotBeNull();
        loginResult!.Success.Should().BeTrue();
        loginResult.User.Should().NotBeNull();
        loginResult.User!.Roles.Should().Contain("farm_admin");
        loginResult.User.Username.Should().Be("adminlogin");
        
        // The tokens should be equivalent (both should work)
        setupResult!.Token.Should().NotBeNullOrEmpty();
        loginResult.Token.Should().NotBeNullOrEmpty();
    }
}