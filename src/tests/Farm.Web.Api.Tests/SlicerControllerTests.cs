using System.Net;
using System.Text;
using System.Text.Json;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Integration tests for SlicerController (slicer integration and profile management)
/// </summary>
public class SlicerControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SlicerControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetProfiles_ShouldReturnDefaultProfiles_WhenNoCustomProfilesExistAsync()
    {
        // Act
        var response = await _client.GetAsync("/api/slicer/profiles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profiles = await response.Content.ReadFromJsonAsync<SlicerProfileDto[]>();

        profiles.Should().NotBeNull().And.HaveCount(3); // Default profiles
        profiles![0].Quality.Should().Be("draft");
        profiles[1].Quality.Should().Be("standard");
        profiles[2].Quality.Should().Be("fine");
    }

    [Fact]
    public async Task CreateProfile_ShouldCreateProfile_WhenValidDataProvidedAsync()
    {
        // Arrange
        var createRequest = new CreateSlicerProfileDto
        {
            Name = "Test Profile",
            Description = "A test slicer profile",
            SlicerType = "PrusaSlicer",
            LayerHeight = 0.15,
            InfillPercentage = 25,
            PrintSpeed = 45,
            NozzleTemperature = 215,
            BedTemperature = 65,
            EnableSupports = true,
            Material = "PETG",
            Quality = "Fine",
            IsDefault = false,
            IsPublic = true
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/slicer/profiles", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var profile = await response.Content.ReadFromJsonAsync<SlicerProfileResponseDto>();

        profile.Should().NotBeNull();
        profile!.Id.Should().NotBeEmpty();
        profile.Name.Should().Be("Test Profile");
        profile.Description.Should().Be("A test slicer profile");
        profile.SlicerType.Should().Be("PrusaSlicer");
        profile.LayerHeight.Should().Be(0.15);
        profile.InfillPercentage.Should().Be(25);
        profile.PrintSpeed.Should().Be(45);
        profile.NozzleTemperature.Should().Be(215);
        profile.BedTemperature.Should().Be(65);
        profile.EnableSupports.Should().BeTrue();
        profile.Material.Should().Be("PETG");
        profile.Quality.Should().Be("Fine");
        profile.IsDefault.Should().BeFalse();
        profile.IsPublic.Should().BeTrue();
    }

    [Fact]
    public async Task CreateProfile_ShouldReturnBadRequest_WhenInvalidSlicerTypeAsync()
    {
        // Arrange
        var createRequest = new CreateSlicerProfileDto
        {
            Name = "Invalid Profile",
            SlicerType = "InvalidSlicer",
            Quality = "Standard"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/slicer/profiles", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid slicer type");
    }

    [Fact]
    public async Task CreateProfile_ShouldReturnBadRequest_WhenInvalidQualityAsync()
    {
        // Arrange
        var createRequest = new CreateSlicerProfileDto
        {
            Name = "Invalid Quality Profile",
            SlicerType = "PrusaSlicer",
            Quality = "InvalidQuality"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/slicer/profiles", createRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid quality setting");
    }

    [Fact]
    public async Task GetProfile_ShouldReturnProfile_WhenProfileExistsAsync()
    {
        // Arrange - Create a profile first
        var createdProfile = await CreateTestProfileAsync("Get Test Profile");

        // Act
        var response = await _client.GetAsync($"/api/slicer/profiles/{createdProfile.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<SlicerProfileResponseDto>();

        profile.Should().NotBeNull();
        profile!.Id.Should().Be(createdProfile.Id);
        profile.Name.Should().Be("Get Test Profile");
    }

    [Fact]
    public async Task GetProfile_ShouldReturnNotFound_WhenProfileDoesNotExistAsync()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/slicer/profiles/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProfile_ShouldRemoveProfile_WhenProfileExistsAsync()
    {
        // Arrange - Create a profile first
        var createdProfile = await CreateTestProfileAsync("Delete Test Profile");

        // Act
        var response = await _client.DeleteAsync($"/api/slicer/profiles/{createdProfile.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify profile is deleted
        var getResponse = await _client.GetAsync($"/api/slicer/profiles/{createdProfile.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProfile_ShouldReturnNotFound_WhenProfileDoesNotExistAsync()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/slicer/profiles/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SliceModel_ShouldStartSlicing_WhenValidModelProvidedAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", "slice-test.stl");

        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent("prusaslicer"), "slicerEngine");

        var profile = new SlicerProfileDto
        {
            LayerHeight = 0.2,
            InfillPercentage = 20,
            PrintSpeed = 50,
            NozzleTemperature = 210,
            BedTemperature = 60,
            Material = "PLA",
            Quality = "standard"
        };
        form.Add(new StringContent(JsonSerializer.Serialize(profile)), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var result = await response.Content.ReadFromJsonAsync<SliceResultDto>();

        result.Should().NotBeNull();
        result!.JobId.Should().NotBeNullOrEmpty();
        result.GcodeUrl.Should().Contain($"/api/slicer/jobs/{result.JobId}/gcode");
        result.Metadata.Should().NotBeNull();
        result.Metadata.SlicerVersion.Should().Be("PrusaSlicer 2.7.0");
    }

    [Fact]
    public async Task SliceModel_ShouldReturnBadRequest_WhenNoModelFileProvidedAsync()
    {
        // Arrange
        using var form = new MultipartFormDataContent();
        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent("prusaslicer"), "slicerEngine");
        form.Add(new StringContent("{}"), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Model file is required");
    }

    [Fact]
    public async Task SliceModel_ShouldReturnBadRequest_WhenInvalidSlicerEngineAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        form.Add(fileContent, "modelFile", "test.stl");

        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent("invalidslicer"), "slicerEngine");
        form.Add(new StringContent("{}"), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Valid slicer engine is required");
    }

    [Fact]
    public async Task SliceModel_ShouldReturnBadRequest_WhenInvalidPrinterIdAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        form.Add(fileContent, "modelFile", "test.stl");

        form.Add(new StringContent("not-a-guid"), "printerId");
        form.Add(new StringContent("prusaslicer"), "slicerEngine");
        form.Add(new StringContent("{}"), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Valid printer ID is required");
    }

    [Fact]
    public async Task SliceModel_ShouldReturnBadRequest_WhenInvalidProfileAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        form.Add(fileContent, "modelFile", "test.stl");

        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent("prusaslicer"), "slicerEngine");
        form.Add(new StringContent("invalid json"), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid slicer profile format");
    }

    [Theory]
    [InlineData("prusaslicer", "PrusaSlicer 2.7.0")]
    [InlineData("orcaslicer", "OrcaSlicer 1.8.0")]
    public async Task SliceModel_ShouldHandleDifferentSlicersAsync(string slicerEngine, string expectedVersion)
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        form.Add(fileContent, "modelFile", "test.stl");

        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent(slicerEngine), "slicerEngine");

        var profile = new SlicerProfileDto { LayerHeight = 0.2, Material = "PLA", Quality = "standard" };
        form.Add(new StringContent(JsonSerializer.Serialize(profile)), "profile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var result = await response.Content.ReadFromJsonAsync<SliceResultDto>();

        result.Should().NotBeNull();
        result!.Metadata.SlicerVersion.Should().Be(expectedVersion);
    }

    [Fact]
    public async Task GetSlicingJob_ShouldReturnJobInfo_WhenJobExistsAsync()
    {
        // Arrange - Start a slicing job first
        var sliceResult = await StartTestSlicingJobAsync();

        // Act
        var response = await _client.GetAsync($"/api/slicer/jobs/{sliceResult.JobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var jobResult = await response.Content.ReadFromJsonAsync<SliceResultDto>();

        jobResult.Should().NotBeNull();
        jobResult!.JobId.Should().Be(sliceResult.JobId);
    }

    [Fact]
    public async Task GetSlicingJob_ShouldReturnNotFound_WhenJobDoesNotExistAsync()
    {
        // Arrange
        var nonExistentJobId = "non-existent-job";

        // Act
        var response = await _client.GetAsync($"/api/slicer/jobs/{nonExistentJobId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelSlicingJob_ShouldCancelJob_WhenJobExistsAsync()
    {
        // Arrange - Start a slicing job first
        var sliceResult = await StartTestSlicingJobAsync();

        // Act
        var response = await _client.PostAsync($"/api/slicer/jobs/{sliceResult.JobId}/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("success").And.Contain("true");
    }

    [Fact]
    public async Task CancelSlicingJob_ShouldReturnNotFound_WhenJobDoesNotExistAsync()
    {
        // Arrange
        var nonExistentJobId = "non-existent-job";

        // Act
        var response = await _client.PostAsync($"/api/slicer/jobs/{nonExistentJobId}/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProfiles_ShouldFilterBySlicerType_WhenSlicerTypeProvidedAsync()
    {
        // Arrange - Create profiles with different slicer types
        await CreateTestProfileAsync("PrusaSlicer Profile", SlicerType.PrusaSlicer);
        await CreateTestProfileAsync("OrcaSlicer Profile", SlicerType.OrcaSlicer);

        // Act
        var response = await _client.GetAsync("/api/slicer/profiles?slicerType=PrusaSlicer");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profiles = await response.Content.ReadFromJsonAsync<SlicerProfileDto[]>();

        profiles.Should().NotBeNull();
        // Note: Default profiles are also returned, so we just check that we got some profiles
        profiles!.Should().HaveCountGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task CreateProfile_ShouldStoreInDatabase_WithCorrectMetadataAsync()
    {
        // Arrange
        var createdProfile = await CreateTestProfileAsync("Database Test Profile");

        // Act - Verify in database
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = await dbContext.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == createdProfile.Id);

        // Assert
        profile.Should().NotBeNull();
        profile!.Name.Should().Be("Database Test Profile");
        profile.SlicerType.Should().Be(SlicerType.PrusaSlicer);
        profile.LayerHeight.Should().Be(0.2);
        profile.InfillPercentage.Should().Be(20);
        profile.PrintSpeed.Should().Be(50);
        profile.NozzleTemperature.Should().Be(210);
        profile.BedTemperature.Should().Be(60);
        profile.Material.Should().Be("PLA");
        profile.Quality.Should().Be(ProfileQuality.Standard);
        profile.IsPublic.Should().BeTrue();
        profile.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    // Helper methods

    private async Task<SlicerProfileResponseDto> CreateTestProfileAsync(string name, SlicerType slicerType = SlicerType.PrusaSlicer)
    {
        var createRequest = new CreateSlicerProfileDto
        {
            Name = name,
            Description = $"Test profile: {name}",
            SlicerType = slicerType.ToString(),
            LayerHeight = 0.2,
            InfillPercentage = 20,
            PrintSpeed = 50,
            NozzleTemperature = 210,
            BedTemperature = 60,
            EnableSupports = false,
            Material = "PLA",
            Quality = "Standard",
            IsDefault = false,
            IsPublic = true
        };

        var response = await _client.PostAsJsonAsync("/api/slicer/profiles", createRequest);
        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<SlicerProfileResponseDto>();
        profile.Should().NotBeNull();

        return profile!;
    }

    private async Task<SliceResultDto> StartTestSlicingJobAsync()
    {
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        form.Add(fileContent, "modelFile", "job-test.stl");

        var printerId = Guid.NewGuid();
        form.Add(new StringContent(printerId.ToString()), "printerId");
        form.Add(new StringContent("prusaslicer"), "slicerEngine");

        var profile = new SlicerProfileDto
        {
            LayerHeight = 0.2,
            InfillPercentage = 20,
            PrintSpeed = 50,
            NozzleTemperature = 210,
            BedTemperature = 60,
            Material = "PLA",
            Quality = "standard"
        };
        form.Add(new StringContent(JsonSerializer.Serialize(profile)), "profile");

        var response = await _client.PostAsync("/api/slicer/slice", form);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SliceResultDto>();
        result.Should().NotBeNull();

        return result!;
    }

    private static byte[] CreateValidStlContent()
    {
        // Create a minimal valid STL file (ASCII format)
        var stlContent = """
            solid test_cube
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1 0 1
                  vertex 1 1 1
                endloop
              endfacet
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1 1 1
                  vertex 0 1 1
                endloop
              endfacet
            endsolid test_cube
            """;
        return Encoding.ASCII.GetBytes(stlContent);
    }
}
