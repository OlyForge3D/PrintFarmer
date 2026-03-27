# Background Service Testing Skill

## Context

.NET `BackgroundService` implementations run continuously with delays between cycles. Testing them requires special patterns to avoid slow tests and race conditions.

## Problem

`PrintFailureMonitorService` (and similar background services) have:
- Initial 30s startup delay
- Continuous polling loop with `Task.Delay`
- Scoped dependencies (DbContext, services)
- External dependencies (HTTP clients, SignalR hubs)
- Cancellation token handling

Testing these naively leads to:
- ❌ Tests that take 30+ seconds
- ❌ Race conditions in assertions
- ❌ Difficulty triggering specific cycles
- ❌ Complex disposal/cleanup logic

## Solution

### 1. Direct ExecuteAsync Invocation

**Instead of:** Starting the hosted service and waiting
**Do:** Invoke `ExecuteAsync` directly with a short-lived cancellation token

```csharp
[Fact]
public async Task Service_OnlyAnalyzesPrintingPrinters()
{
    // Arrange
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var service = CreateTestService(); // Factory method with mocks
    
    // Act — Run ONE cycle then cancel
    var executeTask = service.ExecuteAsync(cts.Token);
    await Task.Delay(1000); // Let one cycle complete
    cts.Cancel();
    
    try {
        await executeTask;
    } catch (OperationCanceledException) {
        // Expected
    }
    
    // Assert
    _mockObicoService.Verify(x => x.AnalyzeImageFromUrlAsync(...), Times.Once);
}
```

### 2. Mock Scoped Dependencies

Background services use `IServiceScopeFactory` to create scopes per cycle. Mock the scope resolution:

```csharp
private readonly Mock<IServiceScopeFactory> _mockScopeFactory = new();
private readonly Mock<IServiceScope> _mockScope = new();
private readonly Mock<IServiceProvider> _mockServiceProvider = new();
private readonly AppDbContext _dbContext; // Real in-memory DB

public PrintFailureMonitorServiceTests()
{
    _mockScopeFactory
        .Setup(f => f.CreateScope())
        .Returns(_mockScope.Object);
    
    _mockScope
        .Setup(s => s.ServiceProvider)
        .Returns(_mockServiceProvider.Object);
    
    _mockServiceProvider
        .Setup(p => p.GetService(typeof(AppDbContext)))
        .Returns(_dbContext);
    
    _mockServiceProvider
        .Setup(p => p.GetService(typeof(IObicoFailureDetectionService)))
        .Returns(_mockObicoService.Object);
}
```

### 3. Control Status Cache

Services often check live printer state. Mock the cache to control eligibility:

```csharp
private readonly Mock<IPrinterStatusCacheReader> _mockStatusCache = new();

// Make printer appear as "Printing"
_mockStatusCache
    .Setup(c => c.GetStatus(printerId))
    .Returns(new PrinterStatusDto {
        IsOnline = true,
        State = "Printing"
    });
```

### 4. Verify SignalR Broadcasts

Mock `IHubContext<THub>` to capture broadcasts:

```csharp
private readonly Mock<IHubContext<PrinterHub>> _mockHub = new();
private readonly Mock<IHubClients> _mockClients = new();
private readonly Mock<IClientProxy> _mockClientProxy = new();

public PrintFailureMonitorServiceTests()
{
    _mockHub.Setup(h => h.Clients).Returns(_mockClients.Object);
    _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
}

[Fact]
public async Task Service_BroadcastsFailureEvent()
{
    // Arrange + Act (run service)...
    
    // Assert
    _mockClientProxy.Verify(
        c => c.SendCoreAsync(
            "FailureDetected",
            It.Is<object[]>(args => 
                args.Length > 0 && 
                ((FailureDetectionDto)args[0]).PrinterId == expectedPrinterId
            ),
            default
        ),
        Times.Once
    );
}
```

### 5. Test Delay Behavior

To verify delay intervals without waiting:

```csharp
// Arrange
var settings = new ObicoSettings { ScanIntervalSeconds = 60 };
var service = new PrintFailureMonitorService(..., Options.Create(settings), ...);

// Act — Run for 2 seconds, observe how many cycles occur
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
await RunServiceAsync(service, cts.Token);

// Assert — With 60s interval, should only run once
_mockObicoService.Verify(x => x.AnalyzeImageFromUrlAsync(...), Times.Once);
```

### 6. Seed Test Data Efficiently

Background services query the database. Use test builders:

```csharp
private async Task<Printer> SeedPrintingPrinterWithCamera(Guid obicoServerId)
{
    var printer = new Printer {
        Id = Guid.NewGuid(),
        Name = "Test Printer",
        ManufacturerId = await GetDefaultManufacturerId(),
        ModelId = await GetDefaultModelId(),
        ServerUrl = $"http://test-{Guid.NewGuid()}.local",
        ObicoServerId = obicoServerId,
        Cameras = new List<Camera> {
            new Camera {
                Id = Guid.NewGuid(),
                Name = "Main Camera",
                SnapshotUrl = "http://cam.local/snapshot",
                IsEnabled = true
            }
        }
    };
    _dbContext.Printers.Add(printer);
    await _dbContext.SaveChangesAsync();
    return printer;
}
```

## When to Use This Pattern

✅ Testing any `BackgroundService` or `IHostedService` implementation  
✅ Services with scoped dependencies (DbContext, repositories)  
✅ Services that poll external APIs or databases  
✅ Services that broadcast via SignalR or message queues  
✅ Services with complex filtering/eligibility logic

## Anti-Patterns

❌ Starting the full host and waiting for delays  
❌ Using `Thread.Sleep` or `Task.Delay` in tests  
❌ Testing multiple cycles in one test (focus on single-cycle behavior)  
❌ Not mocking SignalR hubs (tests will fail silently)

## Related Files

- `src/tests/Farm.Web.Api.Tests/Services/PrintFailureMonitorServiceTests.cs` — Example implementation
- `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs` — Service under test
- `src/tests/Farm.Web.Api.Tests/TestInfrastructure/CustomWebApplicationFactory.cs` — Test factory

## Coverage Philosophy

Focus on:
1. **Eligibility filtering** — Which entities get processed?
2. **External integrations** — Are API calls made with correct parameters?
3. **Broadcasts/events** — Are downstream systems notified?
4. **Error handling** — Does one failure prevent processing others?

Avoid:
- Testing internal implementation details
- Verifying exact log messages (unless critical)
- Testing framework behavior (Task.Delay, cancellation tokens)
