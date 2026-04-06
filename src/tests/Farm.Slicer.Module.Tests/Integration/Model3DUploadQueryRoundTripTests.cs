using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Regression tests for the upload → query round-trip.
/// Validates: after uploading one or more 3D models via HTTP, the query endpoint
/// immediately returns the newly uploaded models in its results.
///
/// User-reported failure: "Success toasts appear after upload, Close becomes
/// 'Please wait', modal disappears, but no files are displayed."
///
/// Root cause candidates:
/// 1. Model saved with IsValid=false → filtered out by query (IsValid=true filter)
/// 2. Model saved to wrong FolderId → filtered out by folder filter
/// 3. Upload returns before DB commit → query races ahead of write
/// 4. Frontend cache/query-key mismatch (covered by ModelUploadModal.test.tsx)
///
/// These tests cover #1–#3 at the HTTP level with a real database.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Regression")]
[Collection(IntegrationTestCollection.Name)]
public class Model3DUploadQueryRoundTripTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public Model3DUploadQueryRoundTripTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private static MultipartFormDataContent CreateStlUpload(string fileName, string content = "solid test\nendsolid test")
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "modelFile", fileName);
        return form;
    }

    private async Task<JsonElement> QueryModelsAsync(string? search = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["page"] = 1,
            ["pageSize"] = 50
        };
        if (search != null)
        {
            payload["search"] = search;
        }

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        HttpResponseMessage response = await _client!.PostAsync("/api/3d-models/query", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "query endpoint should return 200");
        string body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    [Fact]
    public async Task UploadThenQuery_SingleFile_AppearsInQueryResults()
    {
        // Arrange
        string fileName = "regression-single.stl";

        // Act — Upload
        using var uploadContent = CreateStlUpload(fileName);
        HttpResponseMessage uploadResponse = await _client!.PostAsync("/api/3d-models/upload", uploadContent);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created,
            "upload should succeed with 201 Created");

        string uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        JsonElement uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
        string uploadedId = uploadResult.GetProperty("id").GetString()!;

        // Act — Query immediately after upload (no delay)
        JsonElement queryResult = await QueryModelsAsync();

        // Assert — The uploaded model MUST be in the query results
        int totalCount = queryResult.GetProperty("totalCount").GetInt32();
        totalCount.Should().BeGreaterThanOrEqualTo(1,
            "REGRESSION: query must return at least the model we just uploaded");

        JsonElement models = queryResult.GetProperty("models");
        bool found = false;
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.GetProperty("id").GetString() == uploadedId)
            {
                found = true;
                break;
            }
        }

        found.Should().BeTrue(
            "REGRESSION: uploaded model ID {0} must appear in query results. " +
            "If missing, the model was either saved with IsValid=false, committed to a " +
            "different folder than the query filters, or the upload returned before the DB commit.",
            uploadedId);
    }

    [Fact]
    public async Task UploadThenQuery_ThreeFiles_AllAppearInQueryResults()
    {
        // Arrange — Three files, matching the user-reported scenario
        string[] fileNames = ["batch-a.stl", "batch-b.stl", "batch-c.stl"];
        var uploadedIds = new List<string>();

        // Act — Upload all three sequentially (same as frontend serial upload loop)
        foreach (string fileName in fileNames)
        {
            using var uploadContent = CreateStlUpload(fileName);
            HttpResponseMessage response = await _client!.PostAsync("/api/3d-models/upload", uploadContent);
            response.StatusCode.Should().Be(HttpStatusCode.Created,
                $"upload of {fileName} should succeed");

            string body = await response.Content.ReadAsStringAsync();
            JsonElement result = JsonSerializer.Deserialize<JsonElement>(body);
            uploadedIds.Add(result.GetProperty("id").GetString()!);
        }

        uploadedIds.Should().HaveCount(3, "all three uploads should have succeeded");

        // Act — Query immediately after all uploads complete
        JsonElement queryResult = await QueryModelsAsync();

        // Assert — All three models must appear
        int totalCount = queryResult.GetProperty("totalCount").GetInt32();
        totalCount.Should().BeGreaterThanOrEqualTo(3,
            "REGRESSION: query must return at least the 3 models we just uploaded");

        JsonElement models = queryResult.GetProperty("models");
        var returnedIds = new HashSet<string>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            string? id = model.GetProperty("id").GetString();
            if (id != null)
            {
                returnedIds.Add(id);
            }
        }

        foreach (string expectedId in uploadedIds)
        {
            returnedIds.Should().Contain(expectedId,
                "REGRESSION: each uploaded model must appear in query results immediately. " +
                "User symptom: 'success toasts show but no files displayed after modal closes.'");
        }
    }

    [Fact]
    public async Task UploadedModel_HasIsValidTrue_InQueryResults()
    {
        // This test specifically validates that uploads set IsValid=true,
        // since the query endpoint filters by IsValid=true (EfModel3DFileRepository line 76).
        string fileName = "validity-check.stl";

        using var uploadContent = CreateStlUpload(fileName);
        HttpResponseMessage uploadResponse = await _client!.PostAsync("/api/3d-models/upload", uploadContent);
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        string uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        JsonElement uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
        string uploadedId = uploadResult.GetProperty("id").GetString()!;

        // Query the list — if IsValid was set to false, this would return 0 results
        JsonElement queryResult = await QueryModelsAsync();
        JsonElement models = queryResult.GetProperty("models");

        bool modelFound = false;
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.GetProperty("id").GetString() == uploadedId)
            {
                modelFound = true;
                break;
            }
        }

        modelFound.Should().BeTrue(
            "REGRESSION: uploaded model must be queryable. The query endpoint filters by IsValid=true. " +
            "If this fails, UploadModelAsync is saving the model with IsValid=false.");
    }
}
