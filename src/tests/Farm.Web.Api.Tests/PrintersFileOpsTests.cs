using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;
using Farm.Web.Shared;
using System.IO;
using System.Text;
using Moq;
using System.Net.Http.Headers;

namespace Farm.Web.Api.Tests;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class PrintersFileOpsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrintersFileOpsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UploadGcode_returns_typed_resultAsync()
    {
        var client = _factory.CreateClient();

        // Arrange: create a Moonraker printer and mock upload to succeed
        var createDto = new CreatePrinterDto
        {
            Name = "itest-upload",
            ServerUrl = "http://localhost:7125",
            Backend = PrinterBackend.Moonraker
        };
        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();
        var printer = await created.Content.ReadFromJsonAsync<PrinterDto>();
        printer.Should().NotBeNull();

        _factory.MockMoonrakerClient
            .Setup(x => x.UploadGcodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Build multipart/form-data with a small .gcode file
        var content = new MultipartFormDataContent();
        var fileBytes = Encoding.UTF8.GetBytes("; mock gcode\nG28\n");
        var fileContent = new ByteArrayContent(fileBytes)
        {
            Headers = { ContentType = MediaTypeHeaderValue.Parse("application/octet-stream") }
        };
        content.Add(fileContent, name: "file", fileName: "testfile.gcode");

        var resp = await client.PostAsync($"/api/printers/{printer!.Id}/files/upload", content);
        resp.IsSuccessStatusCode.Should().BeTrue();
        var dto = await resp.Content.ReadFromJsonAsync<UploadGcodeResultDto>();
        dto.Should().NotBeNull();
        dto!.Message.Should().Be("File uploaded successfully");
        dto.Filename.Should().Be("testfile.gcode");

        // Cleanup
        var del = await client.DeleteAsync($"/api/printers/{printer.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task StartPrintFromFile_returns_typed_resultAsync()
    {
        var client = _factory.CreateClient();

        // Arrange: create a Moonraker printer and mock start print to succeed
        var createDto = new CreatePrinterDto
        {
            Name = "itest-startprint",
            ServerUrl = "http://localhost:7125",
            Backend = PrinterBackend.Moonraker
        };
        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();
        var printer = await created.Content.ReadFromJsonAsync<PrinterDto>();
        printer.Should().NotBeNull();

        _factory.MockMoonrakerClient
            .Setup(x => x.StartPrintAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var fileName = "benchy.gcode";
        var resp = await client.PostAsync($"/api/printers/{printer!.Id}/files/{fileName}/print", content: null);
        resp.IsSuccessStatusCode.Should().BeTrue();
        var dto = await resp.Content.ReadFromJsonAsync<StartPrintResultDto>();
        dto.Should().NotBeNull();
        dto!.Message.Should().Be("Print started successfully");
        dto.Filename.Should().Be(fileName);

        // Cleanup
        var del = await client.DeleteAsync($"/api/printers/{printer.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }
}
