using Farm.Slicer.Module.Api.Services;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class SlicerProfileImportServiceTests
{
    [Fact]
    public async Task ImportProfilesForModelAsync_DelegatesToSlicersService()
    {
        // Arrange
        var modelId = Guid.NewGuid();
        const string modelName = "Prusa MK4";
        const string manufacturer = "Prusa";
        var slicersMock = new Mock<ISlicersService>();
        slicersMock
            .Setup(s => s.ImportProfilesForModelAsync(modelId, modelName, manufacturer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var sut = new SlicerProfileImportService(slicersMock.Object);

        // Act
        int result = await sut.ImportProfilesForModelAsync(modelId, modelName, manufacturer, CancellationToken.None);

        // Assert
        Assert.Equal(5, result);
        slicersMock.Verify(
            s => s.ImportProfilesForModelAsync(modelId, modelName, manufacturer, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
