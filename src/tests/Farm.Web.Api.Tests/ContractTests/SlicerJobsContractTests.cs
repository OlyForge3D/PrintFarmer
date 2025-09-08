using Xunit.Abstractions;
using System.Text;
using System.Text.Json;

namespace Farm.Web.Api.Tests.ContractTests;

/// <summary>
/// Contract tests for Slicer Jobs API to ensure API compliance with OpenAPI specification
/// These tests validate the external REST contract defined in openapi/slicer-jobs.yaml
/// </summary>
public class SlicerJobsContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;
    private static readonly string[] s_expectedInitialStatuses = ["Queued", "Slicing"]; 

    public SlicerJobsContractTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client = _factory.CreateClient();
        _output = output;
    }

    [Fact]
    public async Task SubmitSlicingJob_WithValidRequest_ShouldReturn202AcceptedAsync()
    {
        // Arrange - Create a test 3D model file
        var testModelContent = CreateTestStlContent();
        var printerId = Guid.NewGuid();
        var slicerProfile = new
        {
            layerHeight = 0.2,
            infillPercentage = 20,
            printSpeed = 50,
            nozzleTemperature = 210,
            bedTemperature = 60,
            supports = false,
            material = "PLA",
            quality = "standard"
        };

        // Create multipart form data
        using var formData = new MultipartFormDataContent();
        formData.Add(new ByteArrayContent(testModelContent), "modelFile", "test-model.stl");
        formData.Add(new StringContent(printerId.ToString()), "printerId");
        formData.Add(new StringContent("OrcaSlicer"), "slicerEngine");
        formData.Add(new StringContent(JsonSerializer.Serialize(slicerProfile)), "slicerProfile");
        formData.Add(new StringContent("Normal"), "priority");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", formData);

        // Assert
        _output.WriteLine($"Response Status: {response.StatusCode}");
        _output.WriteLine($"Response Content: {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var responseContent = await response.Content.ReadAsStringAsync();
        var jobResponse = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        
        Assert.NotNull(jobResponse);
        Assert.True(jobResponse.RootElement.TryGetProperty("jobId", out var jobIdProp));
        Assert.True(Guid.TryParse(jobIdProp.GetString(), out _));
        Assert.True(jobResponse.RootElement.TryGetProperty("status", out var statusProp));
        Assert.Contains(statusProp.GetString(), s_expectedInitialStatuses);
    }

    [Fact]
    public async Task GetJobStatus_WithValidJobId_ShouldReturn200OK()
    {
        // Arrange - First submit a job to get a valid job ID
        var jobId = await SubmitTestJobAndGetIdAsync();

        // Act
    var response = await _client.GetAsync($"/api/slicer/jobs/{jobId}");

        // Assert
        _output.WriteLine($"Response Status: {response.StatusCode}");
        _output.WriteLine($"Response Content: {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var responseContent = await response.Content.ReadAsStringAsync();
        var statusResponse = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        
        Assert.NotNull(statusResponse);
        Assert.True(statusResponse.RootElement.TryGetProperty("jobId", out var jobIdProp));
        Assert.Equal(jobId.ToString(), jobIdProp.GetString());
        Assert.True(statusResponse.RootElement.TryGetProperty("status", out _));
        Assert.True(statusResponse.RootElement.TryGetProperty("progress", out var progressProp));
        Assert.True(progressProp.GetInt32() >= 0 && progressProp.GetInt32() <= 100);
    }

    [Fact]
    public async Task GetJobStatus_WithInvalidJobId_ShouldReturn404NotFound()
    {
        // Arrange
        var invalidJobId = Guid.NewGuid();

        // Act
    var response = await _client.GetAsync($"/api/slicer/jobs/{invalidJobId}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelJob_WithValidJobId_ShouldReturn200OK()
    {
        // Arrange - Submit a job and get its ID
        var jobId = await SubmitTestJobAndGetIdAsync();

        // Act
        var response = await _client.PostAsync($"/api/slicer/jobs/{jobId}/cancel", null);

        // Assert
        _output.WriteLine($"Response Status: {response.StatusCode}");
        _output.WriteLine($"Response Content: {await response.Content.ReadAsStringAsync()}");

        // Should return 200 for successful cancellation or 409 if already completed
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK || 
                   response.StatusCode == System.Net.HttpStatusCode.Conflict);

        if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            var cancelResponse = JsonSerializer.Deserialize<JsonDocument>(responseContent);
            
            Assert.NotNull(cancelResponse);
            Assert.True(cancelResponse.RootElement.TryGetProperty("success", out var successProp));
            Assert.True(successProp.GetBoolean());
        }
    }

    [Fact]
    public async Task CancelJob_WithInvalidJobId_ShouldReturn404NotFound()
    {
        // Arrange
        var invalidJobId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsync($"/api/slicer/jobs/{invalidJobId}/cancel", null);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListSlicerProfiles_ShouldReturn200OK()
    {
        // Act
        var response = await _client.GetAsync("/api/slicer/profiles");

        // Assert
        _output.WriteLine($"Response Status: {response.StatusCode}");
        _output.WriteLine($"Response Content: {await response.Content.ReadAsStringAsync()}");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var responseContent = await response.Content.ReadAsStringAsync();
        var profiles = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        
        Assert.NotNull(profiles);
        Assert.True(profiles.RootElement.ValueKind == JsonValueKind.Array);

        // Validate profile structure if any profiles exist
        if (profiles.RootElement.GetArrayLength() > 0)
        {
            var firstProfile = profiles.RootElement[0];
            Assert.True(firstProfile.TryGetProperty("name", out _));
            Assert.True(firstProfile.TryGetProperty("slicerType", out _));
            Assert.True(firstProfile.TryGetProperty("layerHeight", out _));
        }
    }

    [Fact]
    public async Task ListSlicerProfiles_WithPrinterIdFilter_ShouldReturn200OK()
    {
        // Arrange
        var printerId = Guid.NewGuid();

        // Act  
        var response = await _client.GetAsync($"/api/slicer/profiles?printerId={printerId}");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SubmitSlicingJob_WithInvalidFile_ShouldReturn400BadRequestAsync()
    {
        // Arrange - Create invalid file content
        var invalidContent = Encoding.UTF8.GetBytes("This is not a valid 3D model file");
        var printerId = Guid.NewGuid();

        using var formData = new MultipartFormDataContent();
        formData.Add(new ByteArrayContent(invalidContent), "modelFile", "invalid.txt");
        formData.Add(new StringContent(printerId.ToString()), "printerId");
        formData.Add(new StringContent("OrcaSlicer"), "slicerEngine");
        formData.Add(new StringContent("{}"), "slicerProfile");

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", formData);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitSlicingJob_WithMissingPrinterId_ShouldReturn400BadRequestAsync()
    {
        // Arrange
        var testModelContent = CreateTestStlContent();

        using var formData = new MultipartFormDataContent();
        formData.Add(new ByteArrayContent(testModelContent), "modelFile", "test-model.stl");
        formData.Add(new StringContent("OrcaSlicer"), "slicerEngine");
        formData.Add(new StringContent("{}"), "slicerProfile");
        // Missing printerId

        // Act
        var response = await _client.PostAsync("/api/slicer/slice", formData);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetJobStatus_LegacySingularRoute_ShouldRedirectToPlural()
    {
        var jobId = await SubmitTestJobAndGetIdAsync();
        var noRedirectClient = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var response = await noRedirectClient.GetAsync($"/api/slicer/job/{jobId}");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Found); // 302
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().Be($"/api/slicer/jobs/{jobId}");
    }

    // Helper Methods

    private async Task<Guid> SubmitTestJobAndGetIdAsync()
    {
        var testModelContent = CreateTestStlContent();
        var printerId = Guid.NewGuid();
        var slicerProfile = new { layerHeight = 0.2, infillPercentage = 20 };

        using var formData = new MultipartFormDataContent();
        formData.Add(new ByteArrayContent(testModelContent), "modelFile", "test-model.stl");
        formData.Add(new StringContent(printerId.ToString()), "printerId");
        formData.Add(new StringContent("OrcaSlicer"), "slicerEngine");
        formData.Add(new StringContent(JsonSerializer.Serialize(slicerProfile)), "slicerProfile");

        var response = await _client.PostAsync("/api/slicer/slice", formData);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync();
        var jobResponse = JsonSerializer.Deserialize<JsonDocument>(responseContent);
        
        var jobIdString = jobResponse!.RootElement.GetProperty("jobId").GetString();
        return Guid.Parse(jobIdString!);
    }

    private static byte[] CreateTestStlContent()
    {
        // Create a minimal valid STL file content (ASCII format)
        var stlContent = @"solid test
  facet normal 0 0 1
    outer loop
      vertex 0 0 0
      vertex 1 0 0
      vertex 0 1 0
    endloop
  endfacet
endsolid test";
        
        return Encoding.ASCII.GetBytes(stlContent);
    }
}