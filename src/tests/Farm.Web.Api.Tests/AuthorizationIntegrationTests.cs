using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;
using Farm.Web.Shared;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class AuthorizationIntegrationTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthorizationIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    private async Task<string> CreateAdminAndGetTokenAsync()
    {
        var adminRequest = new
        {
            Username = "authorizationadmin",
            Email = "authorizationadmin@example.com",
            Password = "AdminPassword123!",
            FirstName = "Authorization",
            LastName = "Admin"
        };

        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", adminRequest);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        return result!.Token!;
    }

    private async Task<string> CreateUserAndGetTokenAsync()
    {
        var userRequest = new RegisterRequest(
            "authorizationuser",
            "authorizationuser@example.com",
            "TestPassword123!",
            "Authorization",
            "User");

        var response = await _client.PostAsJsonAsync("/api/auth/register", userRequest);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        return result!.Token!;
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/users/roles")]
    public async Task AdminOnlyEndpoints_WithAdminToken_ShouldReturnSuccessAsync(string endpoint)
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // Act
        var response = await _client.GetAsync(endpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/users/roles")]
    public async Task AdminOnlyEndpoints_WithUserToken_ShouldReturnForbiddenAsync(string endpoint)
    {
        // Arrange
        var userToken = await CreateUserAndGetTokenAsync();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        // Act
        var response = await _client.GetAsync(endpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/users/roles")]
    [InlineData("/api/auth/me")]
    [InlineData("/api/auth/logout")]
    public async Task ProtectedEndpoints_WithoutToken_ShouldReturnUnauthorizedAsync(string endpoint)
    {
        // Act
        var response = await _client.GetAsync(endpoint);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UserCRUD_PostOperations_AdminVsUserAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync();

        var createUserRequest = new CreateUserRequest
        {
            Username = "testcreation",
            Email = "testcreation@example.com",
            Password = "TestCreation123!",
            FirstName = "Test",
            LastName = "Creation"
        };

        // Act & Assert - Admin can create users
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var adminResponse = await _client.PostAsJsonAsync("/api/users", createUserRequest);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // Act & Assert - Regular user cannot create users
        createUserRequest.Username = "testcreation2";
        createUserRequest.Email = "testcreation2@example.com";

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var userResponse = await _client.PostAsJsonAsync("/api/users", createUserRequest);
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UserCRUD_PutOperations_AdminVsUserAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync();

        // Create a user to update
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createUserRequest = new CreateUserRequest
        {
            Username = "testupdate",
            Email = "testupdate@example.com",
            Password = "TestUpdate123!",
            FirstName = "Test",
            LastName = "Update"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/users", createUserRequest);
        var createdUser = await createResponse.Content.ReadFromJsonAsync<UserDto>();

        var updateRequest = new UpdateUserRequest
        {
            FirstName = "Updated",
            LastName = "Name"
        };

        // Act & Assert - Admin can update users
        var adminResponse = await _client.PutAsJsonAsync($"/api/users/{createdUser!.Id}", updateRequest);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act & Assert - Regular user cannot update users
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var userResponse = await _client.PutAsJsonAsync($"/api/users/{createdUser.Id}", updateRequest);
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UserCRUD_DeleteOperations_AdminVsUserAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync();

        // Create a user to delete
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createUserRequest = new CreateUserRequest
        {
            Username = "testdelete",
            Email = "testdelete@example.com",
            Password = "TestDelete123!",
            FirstName = "Test",
            LastName = "Delete"
        };

        var createResponse = await _client.PostAsJsonAsync("/api/users", createUserRequest);
        var createdUser = await createResponse.Content.ReadFromJsonAsync<UserDto>();

        // Act & Assert - Regular user cannot delete users
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var userResponse = await _client.DeleteAsync($"/api/users/{createdUser!.Id}");
        userResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Act & Assert - Admin can delete users
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var adminResponse = await _client.DeleteAsync($"/api/users/{createdUser.Id}");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ProtectedEndpoints_BothRoles_CanAccessUserProfileAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync();

        // Act & Assert - Admin can access their profile
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var adminResponse = await _client.GetAsync("/api/auth/me");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var adminUser = await adminResponse.Content.ReadFromJsonAsync<UserDto>();
        adminUser.Should().NotBeNull();
        adminUser!.Roles.Should().Contain("farm_admin");

        // Act & Assert - Regular user can access their profile
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var userResponse = await _client.GetAsync("/api/auth/me");
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var regularUser = await userResponse.Content.ReadFromJsonAsync<UserDto>();
        regularUser.Should().NotBeNull();
        regularUser!.Roles.Should().Contain("farm_user");
    }

    [Fact]
    public async Task ProtectedEndpoints_BothRoles_CanChangePasswordAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync();

        var adminChangeRequest = new ChangePasswordRequest
        {
            CurrentPassword = "AdminPassword123!",
            NewPassword = "NewAdminPassword123!"
        };

        var userChangeRequest = new ChangePasswordRequest
        {
            CurrentPassword = "TestPassword123!",
            NewPassword = "NewTestPassword123!"
        };

        // Act & Assert - Admin can change password
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var adminResponse = await _client.PostAsJsonAsync("/api/auth/change-password", adminChangeRequest);
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Act & Assert - Regular user can change password
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var userResponse = await _client.PostAsJsonAsync("/api/auth/change-password", userChangeRequest);
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task JWT_TokenValidation_InvalidToken_ShouldReturnUnauthorizedAsync()
    {
        // Arrange - Use invalid JWT token
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "invalid.jwt.token");

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task JWT_TokenValidation_MalformedToken_ShouldReturnUnauthorizedAsync()
    {
        // Arrange - Use malformed token
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-jwt-token");

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authorization_Header_MissingBearer_ShouldReturnUnauthorizedAsync()
    {
        // Arrange - Use wrong authentication scheme
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RoleBasedAuthorization_MultipleRoles_ShouldWorkCorrectlyAsync()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // Act - Admin should be able to access both admin and user endpoints
        var adminOnlyResponse = await _client.GetAsync("/api/users");
        var userResponse = await _client.GetAsync("/api/auth/me");

        // Assert
        adminOnlyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        userResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Admin should have both roles
        var user = await userResponse.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Roles.Should().Contain("farm_admin");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
