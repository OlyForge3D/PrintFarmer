using System.Net;
using System.Text;
using Farm.Web.Api.Data;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Integration tests for ModelController (3D model management)
/// </summary>
public class ModelControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ModelControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task GetModels_ShouldReturnEmptyList_WhenNoModelsExistAsync()
    {
        // Arrange - Clean database
        await CleanDatabaseAsync();

        // Act
        var response = await _client.GetAsync("/api/models");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<Model3DDto[]>();
        models.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task UploadModel_ShouldCreateModel_WhenValidStlFileProvidedAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", "test-cube.stl");

        // Act
        var response = await _client.PostAsync("/api/models", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<Model3DUploadResultDto>();

        result.Should().NotBeNull();
        result!.Id.Should().NotBeEmpty();
        result.Name.Should().Be("test-cube");
        result.FileName.Should().Be("test-cube.stl");
        result.FileSize.Should().Be(stlContent.Length);
        result.FileType.Should().Be("stl");
        result.Url.Should().Be($"/api/models/{result.Id}/file");
    }

    [Fact]
    public async Task UploadModel_ShouldReturnExisting_WhenDuplicateFileUploadedAsync()
    {
        // Arrange - Upload first model
        var stlContent = CreateValidStlContent();
        using var form1 = new MultipartFormDataContent();
        using var fileContent1 = new ByteArrayContent(stlContent);
        fileContent1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form1.Add(fileContent1, "modelFile", "duplicate.stl");

        var firstResponse = await _client.PostAsync("/api/models", form1);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstResult = await firstResponse.Content.ReadFromJsonAsync<Model3DUploadResultDto>();

        // Arrange - Upload same content with different name
        using var form2 = new MultipartFormDataContent();
        using var fileContent2 = new ByteArrayContent(stlContent);
        fileContent2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form2.Add(fileContent2, "modelFile", "duplicate-renamed.stl");

        // Act
        var response = await _client.PostAsync("/api/models", form2);

        // Assert - Should return OK (200) for duplicate, not Created (201)
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Model3DUploadResultDto>();

        result.Should().NotBeNull();
        result!.Id.Should().Be(firstResult!.Id); // Same ID as first upload
        result.Name.Should().Be(firstResult.Name); // Original name preserved
    }

    [Fact]
    public async Task UploadModel_ShouldReturnBadRequest_WhenInvalidFileTypeProvidedAsync()
    {
        // Arrange
        var invalidContent = Encoding.UTF8.GetBytes("This is not a valid 3D model file");
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(invalidContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "modelFile", "invalid.txt");

        // Act
        var response = await _client.PostAsync("/api/models", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid file type");
    }

    [Fact]
    public async Task UploadModel_ShouldReturnBadRequest_WhenNoFileProvidedAsync()
    {
        // Arrange
        using var form = new MultipartFormDataContent();

        // Act
        var response = await _client.PostAsync("/api/models", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Model file is required");
    }

    [Fact]
    public async Task GetModel_ShouldReturnModel_WhenModelExistsAsync()
    {
        // Arrange - Upload a model first
        var uploadResult = await UploadTestModelAsync("get-test.stl");

        // Act
        var response = await _client.GetAsync($"/api/models/{uploadResult.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var model = await response.Content.ReadFromJsonAsync<Model3DDto>();

        model.Should().NotBeNull();
        model!.Id.Should().Be(uploadResult.Id);
        model.Name.Should().Be("get-test");
        model.FileType.Should().Be("stl");
    }

    [Fact]
    public async Task GetModel_ShouldReturnNotFound_WhenModelDoesNotExistAsync()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/models/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetModelFile_ShouldReturnFile_WhenModelExistsAsync()
    {
        // Arrange - Upload a model first
        var uploadResult = await UploadTestModelAsync("download-test.stl");

        // Act
        var response = await _client.GetAsync($"/api/models/{uploadResult.Id}/file");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/vnd.ms-pki.stl");

        var fileContent = await response.Content.ReadAsByteArrayAsync();
        fileContent.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetModelFile_ShouldReturnNotFound_WhenModelDoesNotExistAsync()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/models/{nonExistentId}/file");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteModel_ShouldRemoveModel_WhenModelExistsAsync()
    {
        // Arrange - Upload a model first
        var uploadResult = await UploadTestModelAsync("delete-test.stl");

        // Act
        var response = await _client.DeleteAsync($"/api/models/{uploadResult.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify model is deleted
        var getResponse = await _client.GetAsync($"/api/models/{uploadResult.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteModel_ShouldReturnNotFound_WhenModelDoesNotExistAsync()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.DeleteAsync($"/api/models/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ValidateModel_ShouldReturnValid_WhenValidStlFileProvidedAsync()
    {
        // Arrange
        var stlContent = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(stlContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", "validate-test.stl");

        // Act
        var response = await _client.PostAsync("/api/models/validate", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Model3DValidationResultDto>();

        result.Should().NotBeNull();
        result!.Valid.Should().BeTrue();
        result.Issues.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateModel_ShouldReturnInvalid_WhenInvalidFileTypeProvidedAsync()
    {
        // Arrange
        var invalidContent = Encoding.UTF8.GetBytes("Invalid content");
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(invalidContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "modelFile", "invalid.txt");

        // Act
        var response = await _client.PostAsync("/api/models/validate", form);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<Model3DValidationResultDto>();

        result.Should().NotBeNull();
        result!.Valid.Should().BeFalse();
        result.Issues.Should().NotBeNullOrEmpty();
        result.Issues![0].Should().Contain("Invalid file type");
    }

    [Theory]
    [InlineData("test.3mf", "model/3mf")]
    [InlineData("test.obj", "text/plain")]
    [InlineData("test.ply", "application/octet-stream")]
    public async Task UploadModel_ShouldHandleMultipleFileTypesAsync(string filename, string expectedContentType)
    {
        // Arrange
        var modelContent = CreateValidModelContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(modelContent);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", filename);

        // Act
        var uploadResponse = await _client.PostAsync("/api/models", form);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<Model3DUploadResultDto>();

        // Verify download has correct content type
        var downloadResponse = await _client.GetAsync($"/api/models/{uploadResult!.Id}/file");
        downloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Note: Content type is set based on file extension, not upload type
        var actualContentType = downloadResponse.Content.Headers.ContentType?.MediaType;
        actualContentType.Should().Be(expectedContentType);
    }

    [Fact]
    public async Task GetModels_ShouldReturnMultipleModels_WhenModelsExistAsync()
    {
        // Arrange - Upload several models
        var model1 = await UploadTestModelAsync("list-test-1.stl");
        var model2 = await UploadTestModelAsync("list-test-2.stl");
        var model3 = await UploadTestModelAsync("list-test-3.stl");

        // Act
        var response = await _client.GetAsync("/api/models");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var models = await response.Content.ReadFromJsonAsync<Model3DDto[]>();

        models.Should().NotBeNull().And.HaveCountGreaterOrEqualTo(3);

        var uploadedIds = new[] { model1.Id, model2.Id, model3.Id };
        models!.Where(m => uploadedIds.Contains(m.Id)).Should().HaveCount(3);
    }

    [Fact]
    public async Task UploadModel_ShouldStoreInDatabase_WithCorrectMetadataAsync()
    {
        // Arrange
        var uploadResult = await UploadTestModelAsync("database-test.stl");

        // Act - Verify in database
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var model = await dbContext.Models3D.FirstOrDefaultAsync(m => m.Id == uploadResult.Id);

        // Assert
        model.Should().NotBeNull();
        model!.OriginalFileName.Should().Be("database-test.stl");
        model.DisplayName.Should().Be("database-test");
        model.IsValid.Should().BeTrue();
        model.FileHash.Should().NotBeNullOrEmpty();
        model.FilePath.Should().NotBeNullOrEmpty();
        model.FileSizeBytes.Should().BeGreaterThan(0);
        File.Exists(model.FilePath).Should().BeTrue();
    }

    // Helper methods

    private async Task<Model3DUploadResultDto> UploadTestModelAsync(string filename)
    {
        var content = CreateValidStlContent();
        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", filename);

        var response = await _client.PostAsync("/api/models", form);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<Model3DUploadResultDto>();
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

    private static byte[] CreateValidModelContent()
    {
        // Create generic model content for different file types
        return Encoding.UTF8.GetBytes("Model content for testing");
    }

    /// <summary>
    /// Clean the database by removing all test data
    /// </summary>
    private async Task CleanDatabaseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Remove data in dependency order
        dbContext.PrintJobs.RemoveRange(dbContext.PrintJobs);
        dbContext.PrinterCapabilities.RemoveRange(dbContext.PrinterCapabilities);
        dbContext.Printers.RemoveRange(dbContext.Printers);
        dbContext.GcodeFiles.RemoveRange(dbContext.GcodeFiles);
        dbContext.Models3D.RemoveRange(dbContext.Models3D);
        dbContext.SlicerProfiles.RemoveRange(dbContext.SlicerProfiles);

        await dbContext.SaveChangesAsync();
    }
}
