using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.GcodeHarvest;
using Farm.Modules.Gcode.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Controllers;

public class GcodeHarvestControllerTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, GcodeHarvestQueueItemStatus.Pending)]
    [InlineData(true, GcodeHarvestQueueItemStatus.Completed)]
    public async Task GetQueueAsync_OptionalFilters_ForwardsFiltersAndPreservesResponseShape(
        bool filterPrinter,
        GcodeHarvestQueueItemStatus? status)
    {
        var printerId = Guid.NewGuid();
        Guid? filterPrinterId = filterPrinter ? printerId : null;
        DateTime queuedAt = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        var dto = new GcodeHarvestQueueItemDto(
            Guid.NewGuid(), printerId, "Workshop printer", queuedAt,
            queuedAt.AddSeconds(1), queuedAt.AddSeconds(2),
            status ?? GcodeHarvestQueueItemStatus.Pending, 3, "summary message", 7, 5);
        var queue = new Mock<IGcodeHarvestQueue>(MockBehavior.Strict);
        queue.Setup(service => service.GetQueuedItemsAsync(status, filterPrinterId))
            .ReturnsAsync(new List<GcodeHarvestQueueItemDto> { dto });
        var controller = new GcodeHarvestController(
            Mock.Of<IGcodeHarvestService>(), queue.Object, NullLogger<GcodeHarvestController>.Instance);

        ActionResult<GcodeHarvestQueueItemDto[]> response = await controller.GetQueueAsync(filterPrinterId, status);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        GcodeHarvestQueueItemDto[] items = Assert.IsType<GcodeHarvestQueueItemDto[]>(ok.Value);
        Assert.Equal(dto, Assert.Single(items));
        queue.Verify(service => service.GetQueuedItemsAsync(status, filterPrinterId), Times.Once);
        queue.VerifyNoOtherCalls();

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(items, options));
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        JsonElement item = json.RootElement[0];
        Assert.Equal(
            new[]
            {
                "id", "printerId", "printerName", "queuedAt", "processingStartedAt",
                "completedAt", "status", "priority", "errorMessage", "filesFound", "filesAdded"
            },
            item.EnumerateObject().Select(property => property.Name));
        Assert.Equal(dto.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(dto.PrinterId, item.GetProperty("printerId").GetGuid());
        Assert.Equal("Workshop printer", item.GetProperty("printerName").GetString());
        Assert.Equal(dto.QueuedAt, item.GetProperty("queuedAt").GetDateTime());
        Assert.Equal(dto.ProcessingStartedAt, item.GetProperty("processingStartedAt").GetDateTime());
        Assert.Equal(dto.CompletedAt, item.GetProperty("completedAt").GetDateTime());
        Assert.Equal(dto.Status.ToString(), item.GetProperty("status").GetString());
        Assert.Equal(3, item.GetProperty("priority").GetInt32());
        Assert.Equal("summary message", item.GetProperty("errorMessage").GetString());
        Assert.Equal(7, item.GetProperty("filesFound").GetInt32());
        Assert.Equal(5, item.GetProperty("filesAdded").GetInt32());
    }
}
