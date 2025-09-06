using System.Net;
using System.Net.Http.Json;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

public class UserManagementIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UserManagementIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    private async Task<string> CreateAdminAndGetTokenAsync()
    {
        var adminRequest = new
        {
            Username = "testadmin",
            Email = "testadmin@example.com",
            Password = "AdminPassword123!",
            FirstName = "Test",
            LastName = "Admin"
        };
        
        var response = await _client.PostAsJsonAsync("/api/setup/initial-admin", adminRequest);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        return result!.Token!;
    }

    private async Task<string> CreateUserAndGetTokenAsync(string username = "testuser", string email = "testuser@example.com")
    {
        var userRequest = new RegisterRequest(
            username,
            email,
            "TestPassword123!",
            "Test",
            "User");
        
        var response = await _client.PostAsJsonAsync("/api/auth/register", userRequest);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        return result!.Token!;
    }

    [Fact]
    public async Task GetUsers_AsAdmin_ShouldReturnAllUsers()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        await CreateUserAndGetTokenAsync("user1", "user1@example.com");
        await CreateUserAndGetTokenAsync("user2", "user2@example.com");
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // Act
        var response = await _client.GetAsync("/api/users");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var users = await response.Content.ReadFromJsonAsync<UserDto[]>();
        users.Should().NotBeNull();
        users!.Length.Should().BeGreaterOrEqualTo(3); // admin + 2 users
        
        users.Should().Contain(u => u.Username == "testadmin" && u.Roles.Contains("farm_admin"));
        users.Should().Contain(u => u.Username == "user1" && u.Roles.Contains("farm_user"));
        users.Should().Contain(u => u.Username == "user2" && u.Roles.Contains("farm_user"));
    }

    [Fact]
    public async Task GetUsers_AsRegularUser_ShouldReturnForbidden()
    {
        // Arrange
        var userToken = await CreateUserAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        // Act
        var response = await _client.GetAsync("/api/users");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetUsers_WithoutAuthentication_ShouldReturnUnauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/users");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUser_AsAdmin_ShouldReturnSpecificUser()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync("getuser", "getuser@example.com");
        
        // Get the user ID first
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var targetUser = users!.First(u => u.Username == "getuser");

        // Act
        var response = await _client.GetAsync($"/api/users/{targetUser.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Id.Should().Be(targetUser.Id);
        user.Username.Should().Be("getuser");
        user.Email.Should().Be("getuser@example.com");
        user.Roles.Should().Contain("farm_user");
    }

    [Fact]
    public async Task CreateUser_AsAdmin_ShouldReturnCreatedUser()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createRequest = new CreateUserRequest
        {
            Username = "createduser",
            Email = "created@example.com",
            Password = "CreatedPassword123!",
            FirstName = "Created",
            LastName = "User",
            RoleIds = [] // Will get default role
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/users", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Username.Should().Be("createduser");
        user.Email.Should().Be("created@example.com");
        user.FirstName.Should().Be("Created");
        user.LastName.Should().Be("User");
        user.IsActive.Should().BeTrue();
        user.EmailConfirmed.Should().BeFalse(); // Regular users are not auto-confirmed
    }

    [Fact]
    public async Task CreateUser_AsRegularUser_ShouldReturnForbidden()
    {
        // Arrange
        var userToken = await CreateUserAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var createRequest = new CreateUserRequest
        {
            Username = "forbiddenuser",
            Email = "forbidden@example.com",
            Password = "ForbiddenPassword123!",
            FirstName = "Forbidden",
            LastName = "User"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/users", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateUser_WithDuplicateUsername_ShouldReturnBadRequest()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        await CreateUserAndGetTokenAsync("duplicateuser", "first@example.com");
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createRequest = new CreateUserRequest
        {
            Username = "duplicateuser", // Same username as existing user
            Email = "second@example.com",
            Password = "DuplicatePassword123!",
            FirstName = "Duplicate",
            LastName = "User"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/users", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("already exists");
    }

    [Fact]
    public async Task UpdateUser_AsAdmin_ShouldReturnUpdatedUser()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync("updateuser", "update@example.com");
        
        // Get the user ID first
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var targetUser = users!.First(u => u.Username == "updateuser");

        var updateRequest = new UpdateUserRequest
        {
            FirstName = "Updated",
            LastName = "UserName",
            IsActive = true,
            RoleIds = null // Keep existing roles
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/users/{targetUser.Id}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var updatedUser = await response.Content.ReadFromJsonAsync<UserDto>();
        updatedUser.Should().NotBeNull();
        updatedUser!.Id.Should().Be(targetUser.Id);
        updatedUser.Username.Should().Be("updateuser"); // Username shouldn't change
        updatedUser.FirstName.Should().Be("Updated");
        updatedUser.LastName.Should().Be("UserName");
        updatedUser.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateUser_AsRegularUser_ShouldReturnForbidden()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync("updateuser2", "update2@example.com");
        
        // Get the user ID
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var targetUser = users!.First(u => u.Username == "updateuser2");

        // Switch to regular user token
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        var updateRequest = new UpdateUserRequest
        {
            FirstName = "Forbidden",
            LastName = "Update"
        };

        // Act
        var response = await _client.PutAsJsonAsync($"/api/users/{targetUser.Id}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteUser_AsAdmin_ShouldReturnNoContent()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync("deleteuser", "delete@example.com");
        
        // Get the user ID first
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var targetUser = users!.First(u => u.Username == "deleteuser");

        // Act
        var response = await _client.DeleteAsync($"/api/users/{targetUser.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        
        // Verify user is deleted
        var getResponse = await _client.GetAsync($"/api/users/{targetUser.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteUser_AdminTryingToDeleteSelf_ShouldReturnBadRequest()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        // Get the admin user ID
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var adminUser = users!.First(u => u.Username == "testadmin");

        // Act
        var response = await _client.DeleteAsync($"/api/users/{adminUser.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Cannot delete your own account");
    }

    [Fact]
    public async Task DeleteUser_AsRegularUser_ShouldReturnForbidden()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        var userToken = await CreateUserAndGetTokenAsync("deleteuser2", "delete2@example.com");
        
        // Get the user ID
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        
        var usersResponse = await _client.GetAsync("/api/users");
        var users = await usersResponse.Content.ReadFromJsonAsync<UserDto[]>();
        var targetUser = users!.First(u => u.Username == "deleteuser2");

        // Switch to regular user token
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        // Act
        var response = await _client.DeleteAsync($"/api/users/{targetUser.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetRoles_AsAdmin_ShouldReturnAllRoles()
    {
        // Arrange
        var adminToken = await CreateAdminAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        // Act
        var response = await _client.GetAsync("/api/users/roles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        
        var roles = await response.Content.ReadFromJsonAsync<RoleDto[]>();
        roles.Should().NotBeNull();
        roles!.Should().HaveCountGreaterOrEqualTo(2); // At least farm_admin and farm_user
        
        roles.Should().Contain(r => r.Name == "farm_admin");
        roles.Should().Contain(r => r.Name == "farm_user");
        
        // Verify role details
        var adminRole = roles!.First(r => r.Name == "farm_admin");
        adminRole.DisplayName.Should().NotBeNullOrEmpty();
        adminRole.IsSystemRole.Should().BeTrue();
        adminRole.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetRoles_AsRegularUser_ShouldReturnForbidden()
    {
        // Arrange
        var userToken = await CreateUserAndGetTokenAsync();
        
        _client.DefaultRequestHeaders.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        // Act
        var response = await _client.GetAsync("/api/users/roles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}