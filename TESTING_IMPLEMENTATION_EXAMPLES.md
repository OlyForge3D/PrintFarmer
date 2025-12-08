# Testing Implementation Examples

**Quick Start Guide for Phase 1 Target Services**

---

## 1. PrusaLinkPollingService Test Skeleton

**File:** `src/tests/Farm.Web.Api.Tests/Services/PrusaLinkPollingServiceTests.cs`

```csharp
using Farm.Web.Api.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrusaLinkPollingServiceTests
{
    private readonly Mock<IPrusaLinkClient> _mockClient;
    private readonly Mock<ILogger<PrusaLinkPollingService>> _mockLogger;
    private readonly PrusaLinkPollingService _service;

    public PrusaLinkPollingServiceTests()
    {
        _mockClient = new Mock<IPrusaLinkClient>();
        _mockLogger = new Mock<ILogger<PrusaLinkPollingService>>();
        _service = new PrusaLinkPollingService(_mockClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task StartAsync_InitializesPoller()
    {
        // Arrange - Setup initial state
        
        // Act
        await _service.StartAsync(CancellationToken.None);
        
        // Assert
        _service.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_StopsPolling()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);
        
        // Act
        await _service.StopAsync(CancellationToken.None);
        
        // Assert
        _service.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task PollPrinterStatusAsync_FetchesAndUpdatesStatus()
    {
        // Arrange
        var expectedStatus = new PrinterStatus { IsOnline = true, Temperature = 210 };
        _mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);
        
        // Act
        await _service.PollPrinterStatusAsync(CancellationToken.None);
        
        // Assert
        _mockClient.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandlePollingErrorAsync_RetriesAfterBackoff()
    {
        // Arrange
        _mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection failed"));
        
        // Act & Assert
        await _service.Invoking(s => s.PollPrinterStatusAsync(CancellationToken.None))
            .Should().NotThrowAsync(); // Should handle gracefully
    }

    [Fact]
    public async Task MultiplePollingCycles_MaintainsConsistentState()
    {
        // Arrange
        var statuses = new[] { 
            new PrinterStatus { IsOnline = true, Temperature = 200 },
            new PrinterStatus { IsOnline = true, Temperature = 210 },
            new PrinterStatus { IsOnline = false }
        };
        
        var callCount = 0;
        _mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => statuses[Math.Min(callCount++, statuses.Length - 1)]);
        
        // Act - Run 3 polling cycles
        for (int i = 0; i < 3; i++)
        {
            await _service.PollPrinterStatusAsync(CancellationToken.None);
        }
        
        // Assert
        _mockClient.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
```

**Test Checklist (10 tests):**
- [ ] StartAsync initializes poller
- [ ] StopAsync stops polling
- [ ] PollPrinterStatusAsync fetches status
- [ ] Status is stored/propagated correctly
- [ ] Error handling with retry logic
- [ ] Backoff delays work correctly
- [ ] Multiple cycles maintain state
- [ ] CancellationToken is respected
- [ ] IsRunning property reflects state
- [ ] Logging is called appropriately

---

## 2. ThumbnailGenerationService Test Skeleton

**File:** `src/tests/Farm.Web.Api.Tests/Services/ThumbnailGenerationServiceTests.cs`

```csharp
using Farm.Web.Api.Services;
using FluentAssertions;
using Moq;
using Xunit;
using System.Drawing;

namespace Farm.Web.Api.Tests.Services;

public class ThumbnailGenerationServiceTests
{
    private readonly ThumbnailGenerationService _service;

    public ThumbnailGenerationServiceTests()
    {
        _service = new ThumbnailGenerationService();
    }

    [Fact]
    public async Task GenerateThumbnailAsync_CreatesValidImage()
    {
        // Arrange
        var inputStream = CreateTestImageStream(640, 480);
        
        // Act
        var result = await _service.GenerateThumbnailAsync(inputStream, 128, 128);
        
        // Assert
        result.Should().NotBeNull();
        result.Length.Should().BeGreaterThan(0);
        result.Position.Should().Be(0);
    }

    [Fact]
    public async Task GenerateThumbnailAsync_ResizesCorrectly()
    {
        // Arrange
        var inputStream = CreateTestImageStream(640, 480);
        
        // Act
        var result = await _service.GenerateThumbnailAsync(inputStream, 128, 96);
        
        // Assert - Verify dimensions using image library
        using var resultImage = Image.FromStream(result);
        resultImage.Width.Should().Be(128);
        resultImage.Height.Should().Be(96);
    }

    [Fact]
    public async Task ExtractFromGcodeAsync_ExtractsThumbnailData()
    {
        // Arrange
        var gcodeContent = @"; thumbnail begin 32x32 PNG...
; ... base64 data ...
; thumbnail end";
        var gcodeStream = CreateStreamFromString(gcodeContent);
        
        // Act
        var result = await _service.ExtractFromGcodeAsync(gcodeStream);
        
        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateThumbnailAsync_ReturnsTrueForValidImage()
    {
        // Arrange
        var validImage = CreateTestImageStream(128, 128);
        
        // Act
        var isValid = await _service.ValidateThumbnailAsync(validImage);
        
        // Assert
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateThumbnailAsync_ReturnsFalseForInvalidImage()
    {
        // Arrange
        var invalidStream = CreateStreamFromString("not an image");
        
        // Act
        var isValid = await _service.ValidateThumbnailAsync(invalidStream);
        
        // Assert
        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task CompressThumbnailAsync_ReducesFileSize()
    {
        // Arrange
        var originalStream = CreateTestImageStream(640, 480);
        var originalSize = originalStream.Length;
        
        // Act
        var compressedStream = await _service.CompressThumbnailAsync(originalStream, quality: 80);
        
        // Assert
        compressedStream.Length.Should().BeLessThan(originalSize);
    }

    [Fact]
    public async Task CompressThumbnailAsync_PreservesImageQuality()
    {
        // Arrange
        var originalStream = CreateTestImageStream(640, 480);
        
        // Act
        var compressedStream = await _service.CompressThumbnailAsync(originalStream, quality: 95);
        
        // Assert
        using var resultImage = Image.FromStream(compressedStream);
        resultImage.Width.Should().Be(640);
        resultImage.Height.Should().Be(480);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public async Task GenerateThumbnailAsync_WorksWithVariousSizes(int size)
    {
        // Arrange
        var inputStream = CreateTestImageStream(640, 480);
        
        // Act
        var result = await _service.GenerateThumbnailAsync(inputStream, size, size);
        
        // Assert
        using var resultImage = Image.FromStream(result);
        resultImage.Width.Should().Be(size);
    }

    [Fact]
    public async Task GenerateThumbnailAsync_HandlesNullStream()
    {
        // Act & Assert
        await _service.Invoking(s => s.GenerateThumbnailAsync(null!, 128, 128))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    // Helper methods
    private static MemoryStream CreateTestImageStream(int width, int height)
    {
        var image = new Bitmap(width, height);
        var stream = new MemoryStream();
        image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateStreamFromString(string content)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(content);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
```

**Test Checklist (10 tests):**
- [ ] GenerateThumbnailAsync creates valid image
- [ ] Resizing works correctly
- [ ] ExtractFromGcodeAsync extracts PNG data
- [ ] ValidateThumbnailAsync returns true for valid images
- [ ] ValidateThumbnailAsync returns false for invalid images
- [ ] CompressThumbnailAsync reduces file size
- [ ] Compression preserves quality
- [ ] Works with various thumbnail sizes (Theory test)
- [ ] Handles null stream gracefully
- [ ] Supports common image formats

---

## 3. OctoPrintPollingService Test Skeleton

```csharp
using Farm.Web.Api.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class OctoPrintPollingServiceTests
{
    private readonly Mock<IOctoPrintClient> _mockClient;
    private readonly Mock<ILogger<OctoPrintPollingService>> _mockLogger;
    private readonly OctoPrintPollingService _service;

    public OctoPrintPollingServiceTests()
    {
        _mockClient = new Mock<IOctoPrintClient>();
        _mockLogger = new Mock<ILogger<OctoPrintPollingService>>();
        _service = new OctoPrintPollingService(_mockClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task StartAsync_BeginsPolling()
    {
        // Arrange & Act
        await _service.StartAsync(CancellationToken.None);
        
        // Assert
        _service.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task PollPrinterStatusAsync_FetchesFromOctoPrint()
    {
        // Arrange
        var expectedStatus = new OctoPrintStatus { IsOnline = true };
        _mockClient.Setup(c => c.GetPrinterStateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);
        
        // Act
        await _service.PollPrinterStatusAsync(CancellationToken.None);
        
        // Assert
        _mockClient.Verify(c => c.GetPrinterStateAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdatePrinterStateAsync_NormalizesState()
    {
        // Arrange
        var octoState = "Printing";
        
        // Act
        var normalizedState = await _service.UpdatePrinterStateAsync(octoState);
        
        // Assert
        normalizedState.Should().Be("printing"); // Normalized to lowercase
    }

    [Fact]
    public async Task StopAsync_StopsPolling()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);
        
        // Act
        await _service.StopAsync(CancellationToken.None);
        
        // Assert
        _service.IsActive.Should().BeFalse();
    }

    // ... Add 10+ more tests following Phase 1 pattern
}
```

---

## 4. Common Test Patterns

### Pattern 1: Polling Service Base Tests
```csharp
public abstract class PollingServiceTestBase<TClient, TStatus> 
    where TClient : class
    where TStatus : class
{
    protected Mock<TClient> MockClient { get; }
    protected IPollingService Service { get; set; }

    [Fact]
    public virtual async Task StartAsync_InitializesPoller() { /* ... */ }
    
    [Fact]
    public virtual async Task StopAsync_StopsPolling() { /* ... */ }
    
    [Fact]
    public virtual async Task HandleError_Retries() { /* ... */ }
    
    [Fact]
    public virtual async Task CancellationToken_IsRespected() { /* ... */ }
}

// Usage:
public class PrusaLinkPollingServiceTests : PollingServiceTestBase<IPrusaLinkClient, PrinterStatus>
{
    // Override only specific tests, inherit common ones
}
```

### Pattern 2: Async Service Testing
```csharp
[Fact]
public async Task AsyncOperation_HandlesTimeout()
{
    // Arrange
    var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    
    // Act & Assert
    await _service.Invoking(s => s.LongOperationAsync(cts.Token))
        .Should().ThrowAsync<OperationCanceledException>();
}
```

### Pattern 3: Mocking Dependencies
```csharp
// Setup method chain
_mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
    .ReturnsAsync(new PrinterStatus { IsOnline = true });

// Verify calls
_mockClient.Verify(c => c.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);

// Setup exception throwing
_mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
    .ThrowsAsync(new HttpRequestException());
```

---

## 5. Running Your Tests

```bash
# Run all new Phase 1 tests
dotnet test ./farm-web.sln -c Debug -k "PollingService|Thumbnail|AssetsController"

# Run with coverage
dotnet test ./farm-web.sln -c Debug \
  --collect:"XPlat Code Coverage" \
  --results-directory ./tests/coverage

# Watch mode for development
dotnet watch --project ./tests/Farm.Web.Api.Tests test
```

---

## 6. Coverage Verification

After implementing Phase 1 tests, verify coverage improvement:

```bash
# Run tests with coverage collection
dotnet test ./src/farm-web.sln -c Debug \
  --collect:"XPlat Code Coverage" \
  --results-directory ./src/tests/coverage 2>&1 | tail -20

# Expected output:
# | Farm.Web.Api                   | 29.21% | 24.78% | 35.22% |
# | Total                          | 31.89% | 25.74% | 35.31% |

# Gain: +0.90% method coverage (from 34.41% to 35.31%)
```

---

## 7. Best Practices for Phase 1 Tests

1. **Use Arrange-Act-Assert Pattern**
   ```csharp
   // Arrange - Setup test data and mocks
   // Act - Execute the method under test
   // Assert - Verify results
   ```

2. **One Assertion Per Test (When Possible)**
   ```csharp
   [Fact]
   public void SingleResponsibilityTest() 
   {
       // Test ONE behavior
   }
   ```

3. **Clear Naming Convention**
   ```csharp
   // MethodName_Scenario_ExpectedResult
   public async Task PollAsync_WhenClientThrows_RetriesWithBackoff() { }
   ```

4. **Use Theory for Multiple Scenarios**
   ```csharp
   [Theory]
   [InlineData(100)]
   [InlineData(200)]
   [InlineData(500)]
   public async Task PollAsync_WorksWithVariousIntervals(int intervalMs) { }
   ```

5. **Mock Only External Dependencies**
   ```csharp
   // Good - Mock external service
   Mock<IHttpClient> mockHttp = new();
   
   // Bad - Mock class under test
   // This defeats the purpose of unit testing
   ```

---

## 8. Troubleshooting Common Test Issues

### Issue: Test hangs indefinitely
**Solution:** Add timeout to async operations
```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await _service.SomeAsync(cts.Token);
```

### Issue: Mock not being called
**Solution:** Verify mock setup and arrange order
```csharp
// Correct order: Setup BEFORE executing
_mockClient.Setup(...).Returns(...);
await _service.DoSomething();
_mockClient.Verify(...);
```

### Issue: Test passes locally but fails in CI
**Solution:** 
- Remove assumptions about timing
- Use synchronous waits instead of Thread.Sleep
- Mock all external services

---

## Next Steps

1. **Week 1:** Create test files for Phase 1 services
2. **Week 2:** Implement 40-50 tests
3. **Week 3:** Implement remaining 30-40 tests
4. **Week 4:** Run coverage, verify +1.0-1.2% improvement

**Target:** 70-80 new tests, 35.4-35.6% method coverage

---

See `TESTING_TARGETS_QUICK_REFERENCE.md` for summary and `TESTING_ANALYSIS_SUMMARY.md` for detailed analysis.
